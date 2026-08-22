using Bogus;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetMyBookings;
using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Exercises GetMyBookingsEndpoint end-to-end - hold + confirm a booking as a
// signed-in customer (same flow ConfirmBookingTests uses), then read it back
// through /api/bookings/mine. Same "seed a Unit directly, no real Property
// needed" shortcut ConfirmBookingTests relies on, since Unit.PropertyId is a
// plain Guid with no FK (see Unit.cs/Booking.cs's own notes on this).
[Collection("Integration Tests")]
public class GetMyBookingsTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private static Unit CreateTestUnit(decimal basePrice = 100m)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            UnitType.Room,
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

    private async Task<string> SeedSignedInCustomerAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);
        return signInResult.AccessToken;
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
        HoldAvailabilityResponse? hold = await response.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private async Task<Guid> ConfirmBookingAsAsync(Guid holdId, string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = _faker.Name.FullName(),
                GuestEmail = _faker.Internet.Email()
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.BookingId;
    }

    private async Task<HttpResponseMessage> GetMyBookingsAsync(string? accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/bookings/mine");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetMyBookings_ShouldReturnTheCallersBooking_WithUnitName()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string accessToken = await SeedSignedInCustomerAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);
        Guid bookingId = await ConfirmBookingAsAsync(holdId, accessToken);

        // Act
        HttpResponseMessage response = await GetMyBookingsAsync(accessToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetMyBookingsResponse? result = await response.Content.ReadFromJsonAsync<GetMyBookingsResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        BookingSummary booking = Assert.Single(result.Bookings);
        Assert.Equal(bookingId, booking.BookingId);
        Assert.Equal(unit.Id, booking.UnitId);
        Assert.Equal("Standard Room", booking.UnitName["en"]);
        Assert.Equal(300m, booking.TotalPrice); // 100/night * 3 nights
    }

    [Fact]
    public async Task GetMyBookings_ShouldNotReturnAnotherCustomersBooking()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string ownerToken = await SeedSignedInCustomerAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);
        await ConfirmBookingAsAsync(holdId, ownerToken);

        string otherCustomerToken = await SeedSignedInCustomerAsync();

        // Act
        HttpResponseMessage response = await GetMyBookingsAsync(otherCustomerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetMyBookingsResponse? result = await response.Content.ReadFromJsonAsync<GetMyBookingsResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Empty(result.Bookings);
    }

    [Fact]
    public async Task GetMyBookings_ShouldReturn401_WhenNotAuthenticated()
    {
        // Act
        HttpResponseMessage response = await GetMyBookingsAsync(null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
