using Bogus;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetHostBookings;
using BuildingBlocks.Pagination;
using Availability.Features.HoldAvailability;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// Exercises GetBookingsForHostEndpoint (GET /api/hosts/{hostId}/bookings) -
// the admin-targeted counterpart to GetHostBookingsEndpoint, see docs/adr/0013.
[Collection("Integration Tests")]
public class GetBookingsForHostTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SignInAsSeededAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<(Guid HostId, string AccessToken)> SeedHostUserAsync()
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

        return (becomeHostResult.HostId, becomeHostResult.AccessToken);
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
                TimeZoneId = "Asia/Kuwait",
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
        DateOnly today = CatalogSeeding.Today();
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
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

        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.BookingId;
    }

    [Fact]
    public async Task GetBookingsForHost_ShouldReturnThatHostsBookings_ForAdmin()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid hostId, string hostToken) = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);

        string customerToken = await SeedSignedInCustomerAsync();
        Guid holdId = await HoldUnitAsync(unitId);
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{hostId}/bookings")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<HostBookingSummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<HostBookingSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        HostBookingSummary booking = Assert.Single(result.Items);
        Assert.Equal(bookingId, booking.BookingId);
        Assert.Equal(unitId, booking.UnitId);
    }

    [Fact]
    public async Task GetBookingsForHost_ShouldReturn404_ForNonExistentHost()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{Guid.NewGuid()}/bookings")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBookingsForHost_ShouldReturn403_ForNonAdminCaller()
    {
        (Guid hostId, string hostToken) = await SeedHostUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{hostId}/bookings")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", hostToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
