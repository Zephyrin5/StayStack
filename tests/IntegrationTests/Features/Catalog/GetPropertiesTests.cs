using Bogus;
using BuildingBlocks.Pagination;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.GetProperties;
using Catalog.Features.GetPropertyById;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
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

        return (becomeHostResult.AccessToken, becomeHostResult.HostId);
    }

    private async Task<string> SeedNonHostUserAsync()
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
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(string accessToken, Guid propertyId)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units");
        request.Content = JsonContent.Create(new CreateUnitRequest
        {
            PropertyId = propertyId,
            Name = new Dictionary<string, string> { { "en", "Deluxe Room" } },
            MaxOccupancy = 2,
            BasePrice = 45.5m,
            Currency = Currency.KWD
        });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
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
        PagedResponse<PropertySummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PropertySummary property = Assert.Single(result.Items, p => p.Id == propertyId);
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
        PagedResponse<PropertySummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.All(result.Items, p => Assert.Equal(uniqueCity, p.City));
        Assert.Contains(result.Items, p => p.Id == matchingPropertyId);
    }

    [Fact]
    public async Task GetProperties_ShouldSliceByPageAndReportTotalCount()
    {
        // Arrange - 3 properties sharing one unique city (isolates this
        // test from other seed data in the shared test database, same
        // trick GetProperties_ShouldFilterByCity uses), pageSize 2.
        (string hostAccessToken, _) = await SeedHostUserAsync();
        string uniqueCity = $"City-{Guid.NewGuid():N}";
        Guid firstId = await CreatePropertyAsync(hostAccessToken, uniqueCity);
        Guid secondId = await CreatePropertyAsync(hostAccessToken, uniqueCity);
        Guid thirdId = await CreatePropertyAsync(hostAccessToken, uniqueCity);

        // Act
        HttpResponseMessage page1Response = await _client.GetAsync(
            $"/api/catalog/properties?City={Uri.EscapeDataString(uniqueCity)}&Page=1&PageSize=2", TestContext.Current.CancellationToken);
        HttpResponseMessage page2Response = await _client.GetAsync(
            $"/api/catalog/properties?City={Uri.EscapeDataString(uniqueCity)}&Page=2&PageSize=2", TestContext.Current.CancellationToken);

        // Assert
        PagedResponse<PropertySummary>? page1 =
            await page1Response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        PagedResponse<PropertySummary>? page2 =
            await page2Response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(page1);
        Assert.NotNull(page2);

        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(1, page1.Page);

        Assert.Single(page2.Items);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page2.Page);

        // No overlap/gap between pages - together they cover exactly the
        // 3 seeded ids, once each. This is what the Id tiebreaker in
        // GetPropertiesHandler's OrderBy is actually protecting. Set
        // equality, not sequence equality - deliberately not asserting an
        // expected order here, since that would mean re-deriving Postgres's
        // uuid ordering client-side in .NET, which isn't guaranteed to
        // agree with it.
        List<Guid> allIds = [.. page1.Items.Select(p => p.Id), .. page2.Items.Select(p => p.Id)];
        Assert.Equal(3, allIds.Distinct().Count());
        Assert.Equal(new HashSet<Guid> { firstId, secondId, thirdId }, allIds.ToHashSet());
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
        GetPropertyByIdResponse? result = await response.Content.ReadFromJsonAsync<GetPropertyByIdResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task GetProperties_ShouldReturnPropertiesFromEveryHost_HostIdQueryParamIsNoLongerSupported()
    {
        // GetPropertiesRequest no longer has a HostId field - it used to,
        // shared with GetMyPropertiesEndpoint, but that made "list
        // properties for host X" reachable by any anonymous caller who
        // guessed a host id, not just derived from an authenticated
        // caller's own token. An unrecognized ?HostId= query param is
        // simply ignored by binding, not an error - this asserts the
        // filter genuinely doesn't apply anymore, not just that the
        // request 400s. See GetMyProperties_ShouldReturnOnlyTheCallersOwnProperties
        // for the (correct, auth-derived) host-scoped equivalent.
        // Arrange
        (string firstHostToken, Guid firstHostId) = await SeedHostUserAsync();
        (string secondHostToken, _) = await SeedHostUserAsync();
        Guid firstHostPropertyId = await CreatePropertyAsync(firstHostToken, "Kuwait City");
        Guid secondHostPropertyId = await CreatePropertyAsync(secondHostToken, "Kuwait City");

        // Act
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/catalog/properties?HostId={firstHostId}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PropertySummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains(result.Items, p => p.Id == firstHostPropertyId);
        Assert.Contains(result.Items, p => p.Id == secondHostPropertyId);
    }

    [Fact]
    public async Task GetMyProperties_ShouldReturnOnlyTheCallersOwnProperties()
    {
        // Arrange
        (string firstHostToken, Guid firstHostId) = await SeedHostUserAsync();
        (string secondHostToken, _) = await SeedHostUserAsync();
        Guid firstPropertyId = await CreatePropertyAsync(firstHostToken, "Kuwait City");
        Guid secondPropertyId = await CreatePropertyAsync(firstHostToken, "Al Ahmadi");
        await CreatePropertyAsync(secondHostToken, "Kuwait City");

        // Act
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/properties/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstHostToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PropertySummary>? result = await response.Content.ReadFromJsonAsync<PagedResponse<PropertySummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, p => Assert.Equal(firstHostId, p.HostId));
        Assert.Contains(result.Items, p => p.Id == firstPropertyId);
        Assert.Contains(result.Items, p => p.Id == secondPropertyId);
    }

    [Fact]
    public async Task GetMyProperties_ShouldReturn403_ForNonHostCaller()
    {
        // Arrange
        string nonHostToken = await SeedNonHostUserAsync();

        // Act
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/properties/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHostToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMyProperties_ShouldReturn401_WhenNotAuthenticated()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/catalog/properties/mine", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
