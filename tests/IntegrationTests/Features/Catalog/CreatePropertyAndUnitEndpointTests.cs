using Bogus;
using Catalog;
using Catalog.Features.AdminCreateProperty;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Identity;
using Identity.Entities;
using Identity.Features.Auth.SignIn;
using Identity.Features.BecomeHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// Exercises CreateProperty, AdminCreateProperty and CreateUnit end-to-end
// over real HTTP - unlike HoldAvailabilityHandlerTests/GetPriceCalendarHandlerTests,
// which call their handlers directly. These three are the only Catalog
// handlers currently wired to an actual endpoint, which makes them the
// right place to prove the combined source-generated JsonSerializerContext
// (see Program.cs's UseFastEndpoints call) actually (de)serializes these
// request/response DTOs correctly, not just that everything compiles.
[Collection("Integration Tests")]
public class CreatePropertyAndUnitEndpointTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SeedHostUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password(10)}!";

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
        BecomeHostResponse? becomeHostResult = await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        // Reissued token from BecomeHost, not the original sign-in one -
        // that's the one carrying the host_id claim CreateProperty needs.
        return becomeHostResult.AccessToken;
    }

    private static HttpRequestMessage AuthorizedPost(string path, object body, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task CreateProperty_ShouldReturn200_AndPersistProperty_ForAuthenticatedHost()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        CreatePropertyRequest request = new CreatePropertyRequest
        {
            PropertyType = PropertyType.Hotel,
            Name = new Dictionary<string, string> { { "en", "Seaside Hotel" } },
            City = "Kuwait City"
        };

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost("/api/catalog/properties", request, hostAccessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.PropertyId);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        bool propertyExists = await db.Properties.AnyAsync(p => p.Id == result.PropertyId, TestContext.Current.CancellationToken);
        Assert.True(propertyExists);
    }

    [Fact]
    public async Task AdminCreateProperty_ShouldReturn200_AndAttachPropertyToTargetHost_ForSeededAdmin()
    {
        // Arrange: BecomeHost to get a real HostId to target, then act as
        // the seeded Administrator (see UserConfiguration/RoleConfiguration)
        // rather than that host, to prove admin-on-behalf-of works.
        await SeedHostUserAsync();

        // Read the HostId back from the database rather than decoding the
        // JWT - simpler, and this test only needs the id, not the claim.
        using IServiceScope seedScope = factory.Services.CreateScope();
        var identityDb = seedScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        Guid hostId = await identityDb.Users
            .Where(u => u.HostId != null)
            .OrderByDescending(u => u.Id)
            .Select(u => u.HostId!.Value)
            .FirstAsync(TestContext.Current.CancellationToken);

        HttpResponseMessage adminSignIn = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);
        SignInResponse? adminSignInResult = await adminSignIn.Content.ReadFromJsonAsync<SignInResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(adminSignInResult?.AccessToken);

        AdminCreatePropertyRequest request = new AdminCreatePropertyRequest
        {
            HostId = hostId,
            PropertyType = PropertyType.Chalet,
            Name = new Dictionary<string, string> { { "en", "Desert Chalet" } },
            City = "Al Ahmadi"
        };

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost($"/api/hosts/{hostId}/properties", request, adminSignInResult.AccessToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        var property = await db.Properties.SingleAsync(p => p.Id == result.PropertyId, TestContext.Current.CancellationToken);
        Assert.Equal(hostId, property.HostId);
    }

    [Fact]
    public async Task CreateUnit_ShouldReturn200_AndPersistUnit_UnderCallersOwnProperty()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        HttpResponseMessage propertyResponse = await _client.SendAsync(
            AuthorizedPost("/api/catalog/properties", new CreatePropertyRequest
            {
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Marina Hotel" } }
            }, hostAccessToken),
            TestContext.Current.CancellationToken);
        CreatePropertyResponse? property = await propertyResponse.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(property);

        CreateUnitRequest request = new CreateUnitRequest
        {
            PropertyId = property.PropertyId,
            UnitType = UnitType.Room,
            Name = new Dictionary<string, string> { { "en", "Deluxe Room" } },
            MaxOccupancy = 2,
            BasePrice = 45.5m,
            Currency = "KWD"
        };

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost("/api/catalog/units", request, hostAccessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.UnitId);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        var unit = await db.Units.SingleAsync(u => u.Id == result.UnitId, TestContext.Current.CancellationToken);
        Assert.Equal(property.PropertyId, unit.PropertyId);
    }
}
