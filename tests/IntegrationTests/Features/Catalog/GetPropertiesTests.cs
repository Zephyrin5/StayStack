using Bogus;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.GetProperties;
using Catalog.Features.GetPropertyById;
using Identity.Entities;
using Identity.Features.Auth.SignIn;
using Identity.Features.BecomeHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// Exercises the two new browsing/read endpoints - GetProperties and
// GetPropertyById - the frontend's search/select-property entry point.
// Both are AllowAnonymous, but properties/units still need a real
// authenticated host to create them first, same setup as
// CreatePropertyAndUnitEndpointTests.
[Collection("Integration Tests")]
public class GetPropertiesTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<(string AccessToken, Guid HostId)> SeedHostUserAsync()
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

        using HttpRequestMessage becomeHostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become");
        becomeHostRequest.Content = JsonContent.Create(new BecomeHostRequest
        {
            BusinessName = _faker.Company.CompanyName(),
            ContactEmail = _faker.Internet.Email()
        });
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        return (becomeHostResult.AccessToken, becomeHostResult.HostId);
    }

    private async Task<Guid> CreatePropertyAsync(string accessToken, string city, PropertyType propertyType = PropertyType.Hotel)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/properties");
        request.Content = JsonContent.Create(new CreatePropertyRequest
        {
            PropertyType = propertyType,
            Name = new Dictionary<string, string> { { "en", "Test Property" } },
            City = city
        });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(string accessToken, Guid propertyId)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units");
        request.Content = JsonContent.Create(new CreateUnitRequest
        {
            PropertyId = propertyId,
            UnitType = UnitType.Room,
            Name = new Dictionary<string, string> { { "en", "Deluxe Room" } },
            MaxOccupancy = 2,
            BasePrice = 45.5m,
            Currency = "KWD"
        });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    [Fact]
    public async Task GetProperties_ShouldReturnCreatedProperty_WithoutAuthentication()
    {
        // Arrange
        (string hostAccessToken, Guid hostId) = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken, "Kuwait City");

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/catalog/properties", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetPropertiesResponse? result = await response.Content.ReadFromJsonAsync<GetPropertiesResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PropertySummary property = Assert.Single(result.Properties, p => p.Id == propertyId);
        Assert.Equal(hostId, property.HostId);
    }

    [Fact]
    public async Task GetProperties_ShouldFilterByCity()
    {
        // Arrange
        (string hostAccessToken, _) = await SeedHostUserAsync();
        string uniqueCity = $"City-{Guid.NewGuid():N}";
        Guid matchingPropertyId = await CreatePropertyAsync(hostAccessToken, uniqueCity);
        await CreatePropertyAsync(hostAccessToken, "Some Other City");

        // Act
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/catalog/properties?City={Uri.EscapeDataString(uniqueCity)}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetPropertiesResponse? result = await response.Content.ReadFromJsonAsync<GetPropertiesResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.All(result.Properties, p => Assert.Equal(uniqueCity, p.City));
        Assert.Contains(result.Properties, p => p.Id == matchingPropertyId);
    }

    [Fact]
    public async Task GetPropertyById_ShouldReturnPropertyWithUnits()
    {
        // Arrange
        (string hostAccessToken, Guid hostId) = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostAccessToken, "Kuwait City");
        Guid unitId = await CreateUnitAsync(hostAccessToken, propertyId);

        // Act
        HttpResponseMessage response = await _client.GetAsync($"/api/catalog/properties/{propertyId}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetPropertyByIdResponse? result = await response.Content.ReadFromJsonAsync<GetPropertyByIdResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.Id);
        Assert.Equal(hostId, result.HostId);
        Assert.Contains(result.Units, u => u.Id == unitId);
    }

    [Fact]
    public async Task GetPropertyById_ShouldReturn404_WhenPropertyDoesNotExist()
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/catalog/properties/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
