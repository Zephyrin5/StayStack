using Bogus;
using BuildingBlocks.Pagination;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.GetHostProperties;
using Catalog.Features.GetProperties;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// Exercises GetHostPropertiesEndpoint (GET /api/hosts/{hostId}/properties) -
// the admin-targeted read counterpart to GetMyPropertiesEndpoint, see
// docs/adr/0013.
[Collection("Integration Tests")]
public class GetHostPropertiesTests(IntegrationTestWebApplicationFactory factory)
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

    private async Task<Guid> CreatePropertyAsync(string hostAccessToken, string name = "Seaside Hotel")
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/properties")
        {
            Content = JsonContent.Create(new CreatePropertyRequest
            {
                TimeZoneId = "Asia/Kuwait",
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", name } },
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

    [Fact]
    public async Task GetHostProperties_ShouldReturnThatHostsProperties_ForAdmin()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid hostId, string hostToken) = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{hostId}/properties")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PropertySummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PropertySummary property = Assert.Single(result.Items);
        Assert.Equal(propertyId, property.Id);
        Assert.Equal(hostId, property.HostId);
    }

    [Fact]
    public async Task GetHostProperties_ShouldNotReturnAnotherHostsProperty()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid targetHostId, _) = await SeedHostUserAsync();
        (_, string otherHostToken) = await SeedHostUserAsync();
        await CreatePropertyAsync(otherHostToken);

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{targetHostId}/properties")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PropertySummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetHostProperties_ShouldReturn404_ForNonExistentHost()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{Guid.NewGuid()}/properties")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetHostProperties_ShouldReturn403_ForNonAdminCaller()
    {
        (Guid hostId, string hostToken) = await SeedHostUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/hosts/{hostId}/properties")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", hostToken) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
