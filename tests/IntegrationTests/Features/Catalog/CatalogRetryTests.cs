// AUDIT 2026-09-14: Two tests inject pre-commit (CommitFaults.FailBeforeCommit), three post-commit (CommitFaults.FailAfterCommit), each targeted by a tracked entity; fresh-scope asserts. Probed: the pre-commit pair fail without ChangeTracker.Clear(); the pricing-rule create fails without its recovery lookup.
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.UpdatePricingRule;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// The Catalog half of CancelRetryTests, and it exists because the same defect
// turned up here twice.
//
// A transient failure on COMMIT is exactly what an execution strategy absorbs,
// and it is what exposes work built outside the retried delegate.
// SaveChangesAsync defaults to acceptAllChangesOnSuccess, so the moment it
// returns, a mutation applied to an entity loaded *before* the delegate is
// already the entity's original value. The retry re-applies it, EF sees no
// change, the UPDATE carries audit columns only, the commit succeeds - and the
// handler reports success over a row that was never changed.
//
// Both handlers here did that. Neither failed loudly; both returned 200.
[Collection("Integration Tests")]
public class CatalogRetryTests(IntegrationTestWebApplicationFactory factory)
{
    // Whether a commit carries the entity under test - matched on the tracker,
    // because this host runs TickerQ and a stolen injection leaves the test
    // green while proving nothing. A pricing rule matches by its unit too: a
    // create's rule id is the handler's, unknown to the test.
    private static bool Carries(AppCatalogDbContext context, Guid entityId) =>
        context.ChangeTracker.Entries().Any(entry => entry.Entity switch
        {
            Unit unit => unit.Id == entityId,
            Property property => property.Id == entityId,
            PricingRule rule => rule.Id == entityId || rule.UnitId == entityId,
            _ => false
        });

    // Two cases, and an execution strategy cannot tell them apart. Before the
    // commit, nothing was written and the retry writes it. After it, the work is
    // durable and the caller never finds out - so a handler that assumes a clean
    // slate reports failure for work that succeeded.
    private static CommitFault<AppCatalogDbContext> FailTheCommitCarrying(Guid entityId) =>
        CommitFaults.FailBeforeCommit<AppCatalogDbContext>(context => Carries(context, entityId));

    private static CommitFault<AppCatalogDbContext> LoseTheAckOnTheCommitCarrying(Guid entityId) =>
        CommitFaults.FailAfterCommit<AppCatalogDbContext>(context => Carries(context, entityId));

    private readonly List<Property> _pendingProperties = [];

    private Unit CreateTestUnit()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);
    }

    private async Task SeedAsync(Unit unit)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        context.Add(unit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> SignInAsAdministratorAsync()
    {
        HttpResponseMessage signIn = await factory.CreateClient().PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = IntegrationTestAdmin.Email,
            Password = IntegrationTestAdmin.Password
        }, TestContext.Current.CancellationToken);

        SignInResponse? admin = await signIn.Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(admin?.AccessToken);
        return admin.AccessToken;
    }

    [Fact]
    public async Task AUnitArchiveWhoseCommitFailsOnce_IsStillArchivedAfterTheRetry()
    {
        Unit unit = CreateTestUnit();
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();

        CommitFault<AppCatalogDbContext> commitFailure = FailTheCommitCarrying(unit.Id);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(commitFailure);
        using HttpClient client = host.CreateClient();

        HttpRequestMessage archive = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unit.Id}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Without this the test is vacuous: an injection that never fired, or
        // fired on a background job's transaction, leaves everything below
        // passing for the wrong reason.
        Assert.True(commitFailure.HasFired, "The commit failure never reached the archive.");

        // A fresh scope, never the one the request used. A handler that
        // accepted its changes and then failed to commit leaves an in-memory
        // entity that reads exactly like success - which is the whole trap.
        //
        // IgnoreQueryFilters, because "archived" here means the soft-delete
        // filter hides it; asking for the row directly is what distinguishes
        // "archived" from "deleted" and from "still active".
        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        Unit persisted = await db.Units.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EntityStatus.Archived, persisted.Status);
    }

    [Fact]
    public async Task APricingRuleUpdateWhoseCommitFailsOnce_StillPersistsTheNewPrice()
    {
        // The worse of the two, because this handler runs at Serializable and
        // retries on 40001 as its normal operating mode rather than as an
        // exotic failure - and it carried a comment arguing that not clearing
        // the tracker was deliberate.
        Unit unit = CreateTestUnit();
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();
        HttpClient plain = factory.CreateClient();

        DateOnly start = CatalogSeeding.Today().AddDays(200);

        HttpRequestMessage create = new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/units/{unit.Id}/pricing-rules")
        {
            Content = JsonContent.Create(new CreatePricingRuleRequest
            {
                UnitId = unit.Id,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = start,
                EndDate = start.AddDays(5),
                OverridePrice = 150m
            }, options: TestJsonOptions.Default)
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage created = await plain.SendAsync(create, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        CreatePricingRuleResponse? rule = await created.Content
            .ReadFromJsonAsync<CreatePricingRuleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(rule);

        CommitFault<AppCatalogDbContext> commitFailure = FailTheCommitCarrying(rule.PricingRuleId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(commitFailure);
        using HttpClient client = host.CreateClient();

        HttpRequestMessage update =
            new HttpRequestMessage(HttpMethod.Put, $"/api/catalog/units/{unit.Id}/pricing-rules/{rule.PricingRuleId}")
            {
                Content = JsonContent.Create(new UpdatePricingRuleRequest
                {
                    UnitId = unit.Id,
                    PricingRuleId = rule.PricingRuleId,
                    RuleType = PricingRuleType.DateRangeOverride,
                    StartDate = start,
                    EndDate = start.AddDays(5),
                    OverridePrice = 275m
                }, options: TestJsonOptions.Default)
            };
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(commitFailure.HasFired, "The commit failure never reached the update.");

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        PricingRule persisted = await db.PricingRules.AsNoTracking()
            .SingleAsync(r => r.Id == rule.PricingRuleId, TestContext.Current.CancellationToken);

        // 275, not the 150 it was created with. The old shape returned 200 and
        // left 150 in the database.
        Assert.Equal(275m, persisted.OverridePrice);
    }

    [Fact]
    public async Task APricingRuleCreateWhoseAcknowledgementIsLost_ReturnsTheRuleItAlreadyCreated()
    {
        // The rule committed. The caller was told it conflicts with an existing
        // rule - itself.
        //
        // The factories minted the rule's id inside the retried delegate, and
        // the overlap check ran inside it too, so the retry held a new id and
        // found the first attempt's committed rule overlapping its range.
        Unit unit = CreateTestUnit();
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();

        CommitFault<AppCatalogDbContext> lostAck = LoseTheAckOnTheCommitCarrying(unit.Id);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);
        using HttpClient client = host.CreateClient();

        DateOnly start = CatalogSeeding.Today().AddDays(210);

        HttpRequestMessage create = new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/units/{unit.Id}/pricing-rules")
        {
            Content = JsonContent.Create(new CreatePricingRuleRequest
            {
                UnitId = unit.Id,
                RuleType = PricingRuleType.DateRangeOverride,
                StartDate = start,
                EndDate = start.AddDays(5),
                OverridePrice = 150m
            }, options: TestJsonOptions.Default)
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(create, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the create.");

        CreatePricingRuleResponse? rule = await response.Content
            .ReadFromJsonAsync<CreatePricingRuleResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(rule);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // Exactly one, and the one the caller was handed.
        List<PricingRule> rules = await db.PricingRules.AsNoTracking()
            .Where(r => r.UnitId == unit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(rule.PricingRuleId, Assert.Single(rules).Id);
    }

    [Fact]
    public async Task AUnitArchiveWhoseAcknowledgementIsLost_ReportsTheSuccessItAlreadyAchieved()
    {
        // The archive committed. The caller was told 404.
        //
        // The retry reloaded through the soft-delete query filter, which hides
        // the very row its own previous attempt had just archived, so the
        // handler concluded the unit did not exist. A successful archival
        // reported as a failure - and a host who retries gets the same answer
        // forever, because the second attempt finds it archived too.
        Unit unit = CreateTestUnit();
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();

        CommitFault<AppCatalogDbContext> lostAck = LoseTheAckOnTheCommitCarrying(unit.Id);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);
        using HttpClient client = host.CreateClient();

        HttpRequestMessage archive = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unit.Id}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the archive.");

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        Unit persisted = await db.Units.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EntityStatus.Archived, persisted.Status);
    }

    [Fact]
    public async Task APropertyArchiveWhoseAcknowledgementIsLost_ReportsTheSuccessItAlreadyAchieved()
    {
        // Same exposure across the cascade. The property is archived by the
        // first attempt, so the retry's reload finds nothing behind the filter.
        Unit unit = CreateTestUnit();
        Guid propertyId = unit.PropertyId;
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();

        CommitFault<AppCatalogDbContext> lostAck = LoseTheAckOnTheCommitCarrying(propertyId);
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);
        using HttpClient client = host.CreateClient();

        HttpRequestMessage archive =
            new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the archive.");

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext db = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        Property persisted = await db.Properties.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(p => p.Id == propertyId, TestContext.Current.CancellationToken);

        Assert.Equal(EntityStatus.Archived, persisted.Status);

        // And the cascade still happened - the unit went with it rather than
        // being left live under an archived property.
        Unit persistedUnit = await db.Units.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EntityStatus.Archived, persistedUnit.Status);
    }
}
