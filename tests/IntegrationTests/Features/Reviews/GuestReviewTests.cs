using Bogus;
using Bookings;
using Bookings.Entities;
using Catalog;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reviews;
using Reviews.Entities;
using Reviews.Features.CreateGuestReview;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Reviews;

// Exercises the private host-facing guest-review slice end-to-end - the
// other half of the mutual review pair (see StayReviewTests for the
// guest-facing half). Same "seed a Confirmed booking with an explicit past
// CheckOut directly into AppBookingsDbContext" reasoning StayReviewTests
// uses, since the real Hold/Confirm HTTP flow can't produce a
// checkout-already-passed booking.
[Collection("Integration Tests")]
public class GuestReviewTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SeedHostUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test host user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        using HttpRequestMessage becomeHostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(new BecomeHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email()
            })
        };
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        return becomeHostResult.AccessToken;
    }

    private async Task<string> SignInAsAdministratorAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SignInResponse? result =
            await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<Guid> CreatePropertyAsync(string hostAccessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/properties")
        {
            Content = JsonContent.Create(new CreatePropertyRequest
            {
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Seaside Hotel" } },
                City = "Kuwait City"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAccessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(Guid propertyId, string hostAccessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units")
        {
            Content = JsonContent.Create(new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Standard Room" } },
                MaxOccupancy = 2,
                BasePrice = 100m
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAccessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    private async Task<Guid> SeedBookingAsync(Guid unitId, DateOnly checkIn, DateOnly checkOut)
    {
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), unitId, Guid.NewGuid(), null,
            _faker.Name.FullName(), _faker.Internet.Email(), null, checkIn, checkOut, 2, 300m, Currency.KWD,
            CancellationPolicy.CreateDefault());
        booking.Confirm();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        context.Bookings.Add(booking);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return booking.Id;
    }

    private async Task<HttpResponseMessage> CreateGuestReviewAsync(Guid bookingId, string hostToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/reviews/guests")
        {
            Content = JsonContent.Create(new CreateGuestReviewRequest
            {
                BookingId = bookingId,
                OverallRating = 4,
                Comment = "Considerate guest"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> DeleteGuestReviewAsync(Guid guestReviewId, string adminToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"/api/reviews/guests/{guestReviewId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateGuestReview_ShouldSucceed_ForTheOwningHost()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid bookingId = await SeedBookingAsync(unitId, today.AddDays(-5), today.AddDays(-2));

        // Act
        HttpResponseMessage response = await CreateGuestReviewAsync(bookingId, hostToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateGuestReviewResponse? result = await response.Content.ReadFromJsonAsync<CreateGuestReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.GuestReviewId);
    }

    [Fact]
    public async Task CreateGuestReview_ShouldReturn404_ForANonOwningHost()
    {
        // Arrange
        string ownerHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerHostToken);
        Guid unitId = await CreateUnitAsync(propertyId, ownerHostToken);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid bookingId = await SeedBookingAsync(unitId, today.AddDays(-5), today.AddDays(-2));
        string otherHostToken = await SeedHostUserAsync();

        // Act
        HttpResponseMessage response = await CreateGuestReviewAsync(bookingId, otherHostToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateGuestReview_ShouldReturn400_WhenCheckoutHasNotPassedYet()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid bookingId = await SeedBookingAsync(unitId, today, today.AddDays(3));

        // Act
        HttpResponseMessage response = await CreateGuestReviewAsync(bookingId, hostToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateGuestReview_ShouldReturn409_WhenTheGuestHasAlreadyBeenReviewedForThisBooking()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid bookingId = await SeedBookingAsync(unitId, today.AddDays(-5), today.AddDays(-2));
        await CreateGuestReviewAsync(bookingId, hostToken);

        // Act
        HttpResponseMessage response = await CreateGuestReviewAsync(bookingId, hostToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGuestReview_ShouldArchiveTheReview()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid bookingId = await SeedBookingAsync(unitId, today.AddDays(-5), today.AddDays(-2));
        HttpResponseMessage createResponse = await CreateGuestReviewAsync(bookingId, hostToken);
        CreateGuestReviewResponse? created = await createResponse.Content.ReadFromJsonAsync<CreateGuestReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        string adminToken = await SignInAsAdministratorAsync();

        // Act
        HttpResponseMessage response = await DeleteGuestReviewAsync(created.GuestReviewId, adminToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppReviewsDbContext reviewsDb = scope.ServiceProvider.GetRequiredService<AppReviewsDbContext>();
        GuestReview archived = await reviewsDb.GuestReviews.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == created.GuestReviewId, TestContext.Current.CancellationToken);
        Assert.Equal(EntityStatus.Archived, archived.Status);
    }
}
