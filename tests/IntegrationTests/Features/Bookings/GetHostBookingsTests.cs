using Bogus;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetHostBookings;
using BuildingBlocks.Pagination;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Exercises GetHostBookingsEndpoint end-to-end - real Property/Unit created
// through the actual CreateProperty/CreateUnit endpoints (unlike
// GetMyBookingsTests' "seed a Unit directly" shortcut), since this feature's
// whole point is the cross-module HostId -> Property -> Unit resolution
// (IUnitLookup.GetUnitIdsForHostAsync) - a directly-seeded Unit with no real
// owning Property would trivially pass and prove nothing.
[Collection("Integration Tests")]
public class GetHostBookingsTests(IntegrationTestWebApplicationFactory factory)
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
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

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
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);
        return signInResult.AccessToken;
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

    private async Task<Guid> CreateUnitAsync(Guid propertyId, string hostAccessToken, decimal basePrice = 100m)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units")
        {
            Content = JsonContent.Create(new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Standard Room" } },
                MaxOccupancy = 2,
                BasePrice = basePrice
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAccessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
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

    private async Task<(Guid BookingId, string GuestName, string GuestEmail)> ConfirmBookingAsAsync(Guid holdId, string accessToken)
    {
        string guestName = _faker.Name.FullName();
        string guestEmail = _faker.Internet.Email();

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = guestName,
                GuestEmail = guestEmail
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return (result.BookingId, guestName, guestEmail);
    }

    private async Task<HttpResponseMessage> GetHostBookingsAsync(string? accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/bookings/host");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetHostBookings_ShouldReturnBookingAgainstOwnProperty_WithGuestContactDetails()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);

        string customerToken = await SeedSignedInCustomerAsync();
        Guid holdId = await HoldUnitAsync(unitId);
        (Guid bookingId, string guestName, string guestEmail) = await ConfirmBookingAsAsync(holdId, customerToken);

        // Act
        HttpResponseMessage response = await GetHostBookingsAsync(hostToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<HostBookingSummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<HostBookingSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        HostBookingSummary booking = Assert.Single(result.Items);
        Assert.Equal(bookingId, booking.BookingId);
        Assert.Equal(unitId, booking.UnitId);
        Assert.Equal("Standard Room", booking.UnitName["en"]);
        Assert.Equal(guestName, booking.GuestName);
        Assert.Equal(guestEmail, booking.GuestEmail);
        Assert.Equal(300m, booking.TotalPrice); // 100/night * 3 nights
    }

    [Fact]
    public async Task GetHostBookings_ShouldNotReturnAnotherHostsBooking()
    {
        // Arrange
        string ownerHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerHostToken);
        Guid unitId = await CreateUnitAsync(propertyId, ownerHostToken);

        string customerToken = await SeedSignedInCustomerAsync();
        Guid holdId = await HoldUnitAsync(unitId);
        await ConfirmBookingAsAsync(holdId, customerToken);

        string otherHostToken = await SeedHostUserAsync();

        // Act
        HttpResponseMessage response = await GetHostBookingsAsync(otherHostToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<HostBookingSummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<HostBookingSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetHostBookings_ShouldReturn401_WhenNotAuthenticated()
    {
        // Act
        HttpResponseMessage response = await GetHostBookingsAsync(null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHostBookings_ShouldReturn403_WhenCallerIsNotAHost()
    {
        // Arrange
        string customerToken = await SeedSignedInCustomerAsync();

        // Act
        HttpResponseMessage response = await GetHostBookingsAsync(customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
