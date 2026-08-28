using Bogus;
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.UpdatePricingRule;
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

// ADR-0012/finding #9's Serializable-isolation fix for CreatePricingRuleHandler/
// UpdatePricingRuleHandler is, like HoldAvailabilityConcurrencyTests, a claim
// only a genuinely concurrent test can disprove: a single-threaded test can
// call the handler twice in sequence and see the second call correctly
// rejected, but that's true whether or not the transaction is Serializable -
// a plain Read Committed check-then-insert would look identical under a
// single thread. These fire real concurrent requests, same
// separate-HttpClient-per-request approach as HoldAvailabilityConcurrencyTests,
// to actually exercise the isolation level.
[Collection("Integration Tests")]
public class PricingRuleConcurrencyTests(IntegrationTestWebApplicationFactory factory)
{
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

        using HttpClient signInClient = factory.CreateClient();
        HttpResponseMessage signInResponse = await signInClient.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
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
        HttpResponseMessage becomeHostResponse = await signInClient.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
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

    private async Task<(string HostToken, Guid UnitId)> SeedHostWithUnitAsync()
    {
        string hostToken = await SeedHostUserAsync();

        using HttpClient client = factory.CreateClient();
        HttpResponseMessage propertyResponse = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/properties", hostToken, new CreatePropertyRequest
            {
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Test Property" } },
                City = "Kuwait City"
            }),
            TestContext.Current.CancellationToken);
        CreatePropertyResponse? property = await propertyResponse.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(property);

        HttpResponseMessage unitResponse = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/units", hostToken, new CreateUnitRequest
            {
                PropertyId = property.PropertyId,
                Name = new Dictionary<string, string> { { "en", "Test Unit" } },
                MaxOccupancy = 2,
                BasePrice = 100m,
                Currency = Currency.KWD
            }),
            TestContext.Current.CancellationToken);
        CreateUnitResponse? unit = await unitResponse.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(unit);

        return (hostToken, unit.UnitId);
    }

    [Fact]
    public async Task CreatePricingRule_ConcurrentRequestsForTheSameUnitAndLengthOfStayDiscountType_ExactlyOneSucceeds()
    {
        // LengthOfStayDiscount is the simplest overlap rule to race - at
        // most one is ever allowed per unit (PricingRuleOverlapChecker.
        // EnsureNoLengthOfStayConflict), so N concurrent creates for the
        // same unit are all racing to be the only one that ever gets to
        // exist, not just to avoid a specific date/day overlap.
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();

        const int concurrentRequests = 8;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(i => factory.CreateClient().SendAsync(
                    Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
                    {
                        UnitId = unitId,
                        RuleType = PricingRuleType.LengthOfStayDiscount,
                        MinNights = 3 + i,
                        DiscountPercent = 10m
                    }),
                    TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        int ruleCount = await context.PricingRules.CountAsync(
            r => r.UnitId == unitId && r.RuleType == PricingRuleType.LengthOfStayDiscount, TestContext.Current.CancellationToken);
        Assert.Equal(1, ruleCount);
    }

    [Fact]
    public async Task UpdatePricingRule_ConcurrentUpdatesThatWouldOnlyConflictWithEachOthersNewState_ExactlyOneSucceeds()
    {
        // The write-skew case Serializable isolation exists to catch, that
        // a weaker isolation level (Read Committed, Repeatable Read) would
        // let through: two existing, non-overlapping rules (A and B), each
        // updated concurrently to a new range that only overlaps the
        // OTHER's *current* range, not its own original one. Checked
        // against a snapshot taken before either write, both updates look
        // individually valid - only a real serializable conflict check
        // (not a per-row lock, since A and B are different rows entirely)
        // can catch that applying both together produces two overlapping
        // rules.
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync();

        // A wide gap between the two original ranges (Jan and March,
        // nothing in Feb) is what makes this a genuine write-skew case
        // rather than an ordinary conflict: each update's new range is
        // deliberately chosen to avoid the *other* rule's ORIGINAL range
        // entirely (so a check against a pre-race snapshot finds nothing),
        // while the two NEW ranges overlap each other in February. A naive
        // Read Committed check-then-write would let both through, since
        // each only ever compares itself against the other's stale data.
        CreatePricingRuleResponse ruleA = await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 10), 150m);
        CreatePricingRuleResponse ruleB = await CreateDateRangeOverrideAsync(hostToken, unitId, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), 150m);

        Task<HttpResponseMessage> updateA = factory.CreateClient().SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{ruleA.PricingRuleId}", hostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = ruleA.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 2, 15), // does not reach RuleB's original Mar 1
                OverridePrice = 200m
            }),
            TestContext.Current.CancellationToken);

        Task<HttpResponseMessage> updateB = factory.CreateClient().SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}/pricing-rules/{ruleB.PricingRuleId}", hostToken, new UpdatePricingRuleRequest
            {
                UnitId = unitId,
                PricingRuleId = ruleB.PricingRuleId,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = new DateOnly(2026, 2, 1), // does not reach RuleA's original Jan 10
                EndDate = new DateOnly(2026, 3, 10),
                OverridePrice = 200m
            }),
            TestContext.Current.CancellationToken);

        HttpResponseMessage[] responses = await Task.WhenAll(updateA, updateB);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // Whichever one won, the two rules on record must not overlap -
        // the actual invariant this transaction exists to protect, checked
        // independently of which side of the race happened to succeed.
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        List<PricingRule> rules = await context.PricingRules.AsNoTracking()
            .Where(r => r.UnitId == unitId && r.RuleType == PricingRuleType.DateRangeOverride)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rules.Count);
        bool overlap = rules[0].DateRange!.Value.LowerBound < rules[1].DateRange!.Value.UpperBound
                       && rules[1].DateRange!.Value.LowerBound < rules[0].DateRange!.Value.UpperBound;
        Assert.False(overlap, "The two on-record rules overlap - the write-skew race was not actually prevented.");
    }

    private async Task<CreatePricingRuleResponse> CreateDateRangeOverrideAsync(
        string hostToken, Guid unitId, DateOnly startDate, DateOnly endDate, decimal overridePrice)
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
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
}
