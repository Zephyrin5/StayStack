// AUDIT 2026-09-14: Two tests inject pre-commit (TransactionCommittingAsync), three post-commit (TransactionCommittedAsync), each targeted by a tracked entity; fresh-scope asserts. Probed: the pre-commit pair fail without ChangeTracker.Clear(); the pricing-rule create fails without its recovery lookup.
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.UpdatePricingRule;
using BuildingBlocks.Identity;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Persistence.Interceptors;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data.Common;
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
    // Fails the first Catalog commit carrying one specific entity id, with a
    // SqlState this app classifies as transient, so the delegate re-runs
    // exactly as a real serialization failure would make it.
    //
    // Targeted rather than "the first commit I see": the test host runs
    // TickerQ, and a stolen injection leaves the test green while proving
    // nothing. Substituted for AuditableEntitySaveChangesInterceptor rather
    // than registered as a loose IInterceptor, because each module hands EF
    // that one interceptor by name - a bare IInterceptor registration is never
    // consulted.
    private sealed class FailFirstCatalogCommitFor(ICurrentUserProvider currentUser, TimeProvider timeProvider)
        : AuditableEntitySaveChangesInterceptor(currentUser, timeProvider), IDbTransactionInterceptor
    {
        private static Guid _target;
        private static int _fired;

        public static void ArmFor(Guid entityId)
        {
            _target = entityId;
            Interlocked.Exchange(ref _fired, 0);
        }

        public static bool Fired => Volatile.Read(ref _fired) > 0;

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            bool carriesTarget = _target != Guid.Empty
                                 && eventData.Context is AppCatalogDbContext
                                 && eventData.Context.ChangeTracker.Entries()
                                     .Any(entry => entry.Entity switch
                                     {
                                         Unit unit => unit.Id == _target,
                                         PricingRule rule => rule.Id == _target,
                                         _ => false
                                     });

            if (carriesTarget && Interlocked.Increment(ref _fired) == 1)
            {
                throw new PostgresException(
                    "simulated transient failure on commit", "ERROR", "ERROR", "40001");
            }

            return ValueTask.FromResult(result);
        }
    }

    // The other half of the same failure, and the one that matters more.
    //
    // FailFirstCatalogCommitFor throws from TransactionCommittingAsync -
    // *before* the commit - so it proves the rollback case: nothing was
    // written, the retry writes it. This one throws from
    // TransactionCommittedAsync, after the commit has landed, which is a lost
    // acknowledgement: the work is durable and the caller never finds out.
    //
    // An execution strategy cannot tell the two apart. It re-runs the delegate
    // either way, so the retry meets a world its own previous attempt already
    // changed - and a handler that assumes a clean slate reports failure for
    // work that succeeded.
    private sealed class LoseTheAckOnFirstCatalogCommitFor(ICurrentUserProvider currentUser, TimeProvider timeProvider)
        : AuditableEntitySaveChangesInterceptor(currentUser, timeProvider), IDbTransactionInterceptor
    {
        private static Guid _target;
        private static int _fired;

        public static void ArmFor(Guid entityId)
        {
            _target = entityId;
            Interlocked.Exchange(ref _fired, 0);
        }

        public static bool Fired => Volatile.Read(ref _fired) > 0;

        public Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            // The tracker is checked the same targeted way, and for the same
            // reason: TickerQ commits on its own schedule in this host.
            bool carriesTarget = _target != Guid.Empty
                                 && eventData.Context is AppCatalogDbContext
                                 && eventData.Context.ChangeTracker.Entries()
                                     .Any(entry => entry.Entity switch
                                     {
                                         Unit unit => unit.Id == _target,
                                         Property property => property.Id == _target,
                                         // By unit too: a create's rule id is the
                                         // handler's, unknown to the test arming it.
                                         PricingRule rule => rule.Id == _target || rule.UnitId == _target,
                                         _ => false
                                     });

            if (carriesTarget && Interlocked.Increment(ref _fired) == 1)
            {
                throw new PostgresException(
                    "simulated lost acknowledgement after commit", "ERROR", "ERROR", "40001");
            }

            return Task.CompletedTask;
        }
    }

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

    private static WebApplicationFactory<Program> HostWithFailingFirstCommit(IntegrationTestWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, FailFirstCatalogCommitFor>()));

    private static WebApplicationFactory<Program> HostLosingTheFirstAck(IntegrationTestWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<AuditableEntitySaveChangesInterceptor, LoseTheAckOnFirstCatalogCommitFor>()));

    [Fact]
    public async Task AUnitArchiveWhoseCommitFailsOnce_IsStillArchivedAfterTheRetry()
    {
        Unit unit = CreateTestUnit();
        await SeedAsync(unit);

        string adminToken = await SignInAsAdministratorAsync();

        using WebApplicationFactory<Program> host = HostWithFailingFirstCommit(factory);
        using HttpClient client = host.CreateClient();

        FailFirstCatalogCommitFor.ArmFor(unit.Id);

        HttpRequestMessage archive = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unit.Id}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Without this the test is vacuous: an injection that never fired, or
        // fired on a background job's transaction, leaves everything below
        // passing for the wrong reason.
        Assert.True(FailFirstCatalogCommitFor.Fired, "The commit failure never reached the archive.");

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

        using WebApplicationFactory<Program> host = HostWithFailingFirstCommit(factory);
        using HttpClient client = host.CreateClient();

        FailFirstCatalogCommitFor.ArmFor(rule.PricingRuleId);

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
        Assert.True(FailFirstCatalogCommitFor.Fired, "The commit failure never reached the update.");

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

        using WebApplicationFactory<Program> host = HostLosingTheFirstAck(factory);
        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstCatalogCommitFor.ArmFor(unit.Id);

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
        Assert.True(LoseTheAckOnFirstCatalogCommitFor.Fired, "The lost acknowledgement never reached the create.");

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

        using WebApplicationFactory<Program> host = HostLosingTheFirstAck(factory);
        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstCatalogCommitFor.ArmFor(unit.Id);

        HttpRequestMessage archive = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unit.Id}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LoseTheAckOnFirstCatalogCommitFor.Fired, "The lost acknowledgement never reached the archive.");

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

        using WebApplicationFactory<Program> host = HostLosingTheFirstAck(factory);
        using HttpClient client = host.CreateClient();

        LoseTheAckOnFirstCatalogCommitFor.ArmFor(propertyId);

        HttpRequestMessage archive =
            new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}");
        archive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        HttpResponseMessage response = await client.SendAsync(archive, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LoseTheAckOnFirstCatalogCommitFor.Fired, "The lost acknowledgement never reached the archive.");

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
