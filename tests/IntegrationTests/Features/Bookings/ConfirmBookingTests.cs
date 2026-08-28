using Bogus;
using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Exercises the frontend-relevant end-to-end path: hold a unit via the now-
// wired HoldAvailabilityEndpoint, then confirm it into a Booking via
// ConfirmBookingEndpoint - both over real HTTP, matching
// CreatePropertyAndUnitEndpointTests' approach.
[Collection("Integration Tests")]
public class ConfirmBookingTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private static Unit CreateTestUnit(decimal basePrice = 100m)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            basePrice);
    }

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> HoldUnitAsync(Guid unitId)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/catalog/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HoldAvailabilityResponse? hold = await response.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private static ConfirmBookingRequest CreateValidRequest(Guid holdId)
    {
        return new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com",
            GuestPhone = "+965 1234 5678"
        };
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn200_AndPersistPendingBooking_AndFlipHoldToBooked()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(BookingStatus.Pending, result.BookingStatus);
        Assert.Equal(300m, result.TotalPrice); // 100/night * 3 nights
        Assert.Equal(Currency.KWD, result.Currency);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == result.BookingId, TestContext.Current.CancellationToken);
        Assert.Equal(unit.Id, booking.UnitId);
        Assert.Equal(holdId, booking.HoldId);
        Assert.Null(booking.CustomerId);
        Assert.Equal("jane@example.com", booking.GuestEmail);
        Assert.Equal(2, booking.GuestCount); // from the hold, not re-collected at confirm time

        AppCatalogDbContext catalogDb = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        UnitAvailabilityHold persistedHold = await catalogDb.UnitAvailabilityHolds
            .AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
        Assert.Equal("booked", persistedHold.Status);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldUsePriceLockedAtHoldTime_NotUnitsCurrentPrice()
    {
        // A hold snapshots total_price/currency at HoldAvailabilityHandler
        // time - if the unit's base price changes afterward, confirming
        // that hold must still charge what the customer saw when they held
        // it, not the unit's new price.
        // Arrange
        Unit unit = CreateTestUnit(100m);
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id); // 100/night * 3 nights = 300

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalogDb = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            Unit trackedUnit = await catalogDb.Units.SingleAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken);
            trackedUnit.SetBasePrice(500m);
            await catalogDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(300m, result.TotalPrice); // the price at hold time, not 1500m (the new price)
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn404_WhenHoldIdDoesNotExist()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn404_WhenHoldIsExpired()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        UnitAvailabilityHold expiredHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false),
            Status = "held",
            HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-16),
            GuestCount = 2,
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };
        await SeedCatalogAsync(unit, expiredHold);

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(expiredHold.Id), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn404_WhenHoldAlreadyConfirmed()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        HttpResponseMessage firstResponse = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act - confirm the same hold a second time
        HttpResponseMessage secondResponse = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldSucceed_WithNullCustomerId_ForGuestCheckout()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        // Act - no Authorization header
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == result.BookingId, TestContext.Current.CancellationToken);
        Assert.Null(booking.CustomerId);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldSetCustomerId_ForAuthenticatedCaller()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope seedScope = factory.Services.CreateScope();
        var userManager = seedScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        using HttpRequestMessage confirmRequest = new HttpRequestMessage(HttpMethod.Post, "/api/bookings");
        confirmRequest.Content = JsonContent.Create(CreateValidRequest(holdId));
        confirmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(confirmRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == result.BookingId, TestContext.Current.CancellationToken);
        Assert.Equal(user.Id, booking.CustomerId);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenGuestEmailIsInvalid()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid holdId = await HoldUnitAsync(unit.Id);

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateValidRequest(holdId) with { GuestEmail = "not-an-email" }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
