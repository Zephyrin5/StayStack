using Bogus;
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.ListPricingRules;
using Catalog.Features.UpdatePricingRule;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// End-to-end over real HTTP, same reasoning as
// UpdateDeletePropertyAndUnitEndpointTests for going through the endpoints
// rather than the handlers directly - ownership enforcement is what these
// mostly exist to prove, and that lives in the endpoint/auth pipeline as
// much as the handler.
[Collection("Integration Tests")]
public class PricingRuleHandlerTests(IntegrationTestWebApplicationFactory factory)
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
        becomeHostRequest.Content = JsonContent.Create(new Identity.Features.BecomeHost.BecomeHostRequest
        {
            BusinessName = _faker.Company.CompanyName(),
            ContactEmail = _faker.Internet.Email()
        });
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        Identity.Features.BecomeHost.BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<Identity.Features.BecomeHost.BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
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

    private async Task<Guid> CreatePropertyAsync(string accessToken)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/properties", accessToken, new CreatePropertyRequest
            {
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Test Property" } },
                City = "Kuwait City"
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
                Name = new Dictionary<string, string> { { "en", "Test Unit" } },
                MaxOccupancy = 2,
                BasePrice = 100m,
                Currency = Currency.KWD
            }),
            TestContext.Current.CancellationToken);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    private async Task<(string HostToken, Guid UnitId)> SeedHostWithUnitAsync()
    {
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(hostToken, propertyId);
        return (hostToken, unitId);
    }

    private async Task<CreatePricingRuleResponse> CreateDateRangeOverrideAsync(
        string accessToken, Guid unitId, DateOnly startDate, DateOnly endDate, decimal overridePrice = 250m)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", accessToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = startDate,
                EndDate = endDate,
                OverridePrice = overridePrice
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePricingRuleResponse? result = await response.Content.ReadFromJsonAsync<CreatePricingRuleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public async Task Create_ShouldReturn200_AndPersistRule_ForOwningHost()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DayOfWeekMultiplier,
                DaysOfWeek = [5, 6],
                Multiplier = 1.5m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePricingRuleResponse? result = await response.Content.ReadFromJsonAsync<CreatePricingRuleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        PricingRule rule = await db.PricingRules.SingleAsync(r => r.Id == result.PricingRuleId, TestContext.Current.CancellationToken);
        Assert.Equal(PricingRuleType.DayOfWeekMultiplier, rule.RuleType);
        Assert.Equal([5, 6], rule.DaysOfWeek ?? []);
        Assert.Equal(1.5m, rule.Multiplier);
    }

    [Fact]
    public async Task Create_ShouldReturn404_ForNonOwningHost()
    {
        (string ownerToken, Guid unitId) = await SeedHostWithUnitAsync();
        string otherHostToken = await SeedHostUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", otherHostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.LengthOfStayDiscount,
                MinNights = 7,
                DiscountPercent = 10m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldReturn409_WhenDateRangeOverrideOverlapsExistingOne()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 12, 25),
                EndDate = new DateOnly(2027, 1, 2),
                OverridePrice = 300m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldSucceed_WhenDateRangeOverrideDoesNotOverlap()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 12, 26),
                EndDate = new DateOnly(2026, 12, 31),
                OverridePrice = 300m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldReturn409_WhenDayOfWeekMultiplierSharesAWeekday()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DayOfWeekMultiplier,
                DaysOfWeek = [5, 6],
                Multiplier = 1.5m
            }),
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.DayOfWeekMultiplier,
                DaysOfWeek = [0, 6],
                Multiplier = 1.2m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldReturn409_WhenSecondLengthOfStayDiscountRuleAdded()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.LengthOfStayDiscount,
                MinNights = 7,
                DiscountPercent = 10m
            }),
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.LengthOfStayDiscount,
                MinNights = 30,
                DiscountPercent = 20m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Update_ShouldReturn200_AndPersistChanges_ForOwningHost()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26), 250m);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", hostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = created.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 12, 21),
                EndDate = new DateOnly(2026, 12, 27),
                OverridePrice = 275m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        PricingRule rule = await db.PricingRules.SingleAsync(r => r.Id == created.PricingRuleId, TestContext.Current.CancellationToken);
        Assert.Equal(275m, rule.OverridePrice);
        Assert.Equal(new DateOnly(2026, 12, 21), rule.DateRange!.Value.LowerBound);
        Assert.Equal(new DateOnly(2026, 12, 27), rule.DateRange.Value.UpperBound);
    }

    [Fact]
    public async Task Update_ShouldSucceed_WhenRangeOnlyOverlapsItself()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26), 250m);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", hostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = created.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 12, 20),
                EndDate = new DateOnly(2026, 12, 26),
                OverridePrice = 300m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_ShouldReturn409_WhenChangedRangeOverlapsAnotherRule()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 10));
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            hostToken, unitId, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 10));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", hostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = created.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2027, 1, 5),
                EndDate = new DateOnly(2027, 1, 15),
                OverridePrice = 300m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Update_ShouldReturn404_ForNonOwningHost()
    {
        (string ownerToken, Guid unitId) = await SeedHostWithUnitAsync();
        string otherHostToken = await SeedHostUserAsync();
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            ownerToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", otherHostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = created.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 12, 20),
                EndDate = new DateOnly(2026, 12, 26),
                OverridePrice = 1m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ShouldArchiveRule_AndExcludeItFromList()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));

        HttpResponseMessage deleteResponse = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", hostToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext db = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        PricingRule archived = await db.PricingRules.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == created.PricingRuleId, TestContext.Current.CancellationToken);
        Assert.Equal(EntityStatus.Archived, archived.Status);

        HttpResponseMessage listResponse = await _client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/catalog/units/{unitId}/pricing-rules", hostToken),
            TestContext.Current.CancellationToken);
        ListPricingRulesResponse? listResult =
            await listResponse.Content.ReadFromJsonAsync<ListPricingRulesResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(listResult);
        Assert.Empty(listResult.Rules);
    }

    [Fact]
    public async Task Delete_ShouldReturn404_ForNonOwningHost()
    {
        (string ownerToken, Guid unitId) = await SeedHostWithUnitAsync();
        string otherHostToken = await SeedHostUserAsync();
        CreatePricingRuleResponse created = await CreateDateRangeOverrideAsync(
            ownerToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/units/{unitId}/pricing-rules/{created.PricingRuleId}", otherHostToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ShouldReturnOnlyThisUnitsActiveRules()
    {
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        Guid propertyId2 = await CreatePropertyAsync(hostToken);
        Guid otherUnitId = await CreateUnitAsync(hostToken, propertyId2);

        await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));
        await CreateDateRangeOverrideAsync(hostToken, otherUnitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26));

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/catalog/units/{unitId}/pricing-rules", hostToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListPricingRulesResponse? result =
            await response.Content.ReadFromJsonAsync<ListPricingRulesResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PricingRuleSummary onlyRule = Assert.Single(result.Rules);
        Assert.Equal(PricingRuleType.DateRangeOverride, onlyRule.RuleType);
    }
}
