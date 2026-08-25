using Bogus;
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.DeleteProperty;
using Catalog.Features.DeleteUnit;
using Catalog.Features.UpdateProperty;
using Catalog.Features.UpdateUnit;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// UpdateProperty/DeleteProperty/UpdateUnit/DeleteUnit end-to-end over real
// HTTP - same reasoning as CreatePropertyAndUnitEndpointTests for going
// through the endpoint rather than the handler directly.
[Collection("Integration Tests")]
public class UpdateDeletePropertyAndUnitEndpointTests(IntegrationTestWebApplicationFactory factory)
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

        using HttpRequestMessage becomeHostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become");
        becomeHostRequest.Content = JsonContent.Create(new BecomeHostRequest
        {
            BusinessName = _faker.Company.CompanyName(),
            ContactEmail = _faker.Internet.Email()
        });
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        return becomeHostResult.AccessToken;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken, object? body = null)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<Guid> CreatePropertyAsync(string accessToken, string city = "Kuwait City", PropertyType propertyType = PropertyType.Hotel)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/properties", accessToken, new CreatePropertyRequest
            {
                PropertyType = propertyType,
                Name = new Dictionary<string, string> { { "en", "Original Name" } },
                City = city
            }),
            TestContext.Current.CancellationToken);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(string accessToken, Guid propertyId)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/units", accessToken, new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Original Unit" } },
                MaxOccupancy = 2,
                BasePrice = 45.5m,
                Currency = Currency.KWD
            }),
            TestContext.Current.CancellationToken);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    [Fact]
    public async Task UpdateProperty_ShouldReturn200_AndPersistChanges_ForOwningHost()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken, city: "Kuwait City", propertyType: PropertyType.Hotel);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/properties/{propertyId}", hostAccessToken, new UpdatePropertyRequest
            {
                PropertyId = propertyId,
                PropertyType = PropertyType.Chalet,
                Name = new Dictionary<string, string> { { "en", "Renamed Property" } },
                City = "Salmiya"
            }),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        Property property = await db.Properties.SingleAsync(p => p.Id == propertyId, TestContext.Current.CancellationToken);
        Assert.Equal(PropertyType.Chalet, property.PropertyType);
        Assert.Equal("Salmiya", property.City);
        Assert.Equal("Renamed Property", property.Name.Values["en"]);
    }

    [Fact]
    public async Task UpdateProperty_ShouldReturn404_ForNonOwningHost()
    {
        // Arrange
        string ownerToken = await SeedHostUserAsync();
        string otherHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/properties/{propertyId}", otherHostToken, new UpdatePropertyRequest
            {
                PropertyId = propertyId,
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Hijacked" } }
            }),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProperty_ShouldArchivePropertyAndItsUnits_AndRemoveThemFromListings()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken);
        Guid unitId = await CreateUnitAsync(hostAccessToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}", hostAccessToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        Assert.Equal(EntityStatus.Archived, (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(EntityStatus.Archived, (await db.Units.IgnoreQueryFilters().SingleAsync(u => u.Id == unitId, TestContext.Current.CancellationToken)).Status);

        // The global soft-delete query filter is what the rest of the API
        // relies on to treat this as gone - proven through the actual
        // public read endpoint, not just by inspecting Status directly.
        HttpResponseMessage getResponse = await _client.GetAsync($"/api/catalog/properties/{propertyId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteProperty_ShouldReturn404_ForNonOwningHost()
    {
        // Arrange
        string ownerToken = await SeedHostUserAsync();
        string otherHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}", otherHostToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProperty_ShouldReturn200_ForAdministratorTargetingAnyHostsProperty()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken);

        HttpResponseMessage adminSignIn = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);
        SignInResponse? adminSignInResult = await adminSignIn.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(adminSignInResult?.AccessToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}", adminSignInResult.AccessToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUnit_ShouldReturn200_AndPersistChanges_ForOwningHost()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken);
        Guid unitId = await CreateUnitAsync(hostAccessToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}", hostAccessToken, new UpdateUnitRequest
            {
                UnitId = unitId,
                Name = new Dictionary<string, string> { { "en", "Renamed Unit" } },
                MaxOccupancy = 4,
                BasePrice = 99.9m,
                Currency = Currency.SAR
            }),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        Unit unit = await db.Units.SingleAsync(u => u.Id == unitId, TestContext.Current.CancellationToken);
        Assert.Equal(4, unit.MaxOccupancy);
        Assert.Equal(99.9m, unit.BasePrice);
        Assert.Equal(Currency.SAR, unit.Currency);
        Assert.Equal("Renamed Unit", unit.Name.Values["en"]);
    }

    [Fact]
    public async Task UpdateUnit_ShouldReturn404_ForNonOwningHost()
    {
        // Arrange
        string ownerToken = await SeedHostUserAsync();
        string otherHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerToken);
        Guid unitId = await CreateUnitAsync(ownerToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}", otherHostToken, new UpdateUnitRequest
            {
                UnitId = unitId,
                Name = new Dictionary<string, string> { { "en", "Hijacked" } },
                MaxOccupancy = 2,
                BasePrice = 10m,
                Currency = Currency.KWD
            }),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUnit_ShouldArchiveUnit_AndLeaveItsPropertyActive()
    {
        // Arrange
        string hostAccessToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken);
        Guid unitId = await CreateUnitAsync(hostAccessToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/units/{unitId}", hostAccessToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        Assert.Equal(EntityStatus.Archived, (await db.Units.IgnoreQueryFilters().SingleAsync(u => u.Id == unitId, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(EntityStatus.Active, (await db.Properties.SingleAsync(p => p.Id == propertyId, TestContext.Current.CancellationToken)).Status);

        HttpResponseMessage getResponse = await _client.GetAsync($"/api/catalog/properties/{propertyId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUnit_ShouldReturn404_ForNonOwningHost()
    {
        // Arrange
        string ownerToken = await SeedHostUserAsync();
        string otherHostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(ownerToken);
        Guid unitId = await CreateUnitAsync(ownerToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/units/{unitId}", otherHostToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
