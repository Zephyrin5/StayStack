using Availability;
using Availability.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Availability;

// This is the highest-value concurrency test in the system: proves the
// Postgres exclusion constraint - not application code - is what actually
// makes double-booking impossible. HoldAvailabilityHandler's own exclusion-
// violation catch is only meaningful if a real race can actually reach it,
// which a single-threaded test can never exercise. Fires genuinely
// concurrent requests (separate HttpClients/connections, same as
// TransactionsTests' equivalent for the transactions-in-progress index) at
// the exact same unit/date range and asserts the database - not a
// pre-check, not a lock - lets exactly one through.
[Collection("Integration Tests")]
public class HoldAvailabilityConcurrencyTests(IntegrationTestWebApplicationFactory factory)
{
    private static (Property Property, Unit Unit) CreateTestUnit(int maxCapacity = 10)
    {
        // A real Property, not a throwaway id - see CatalogSeeding.
        Property property = CatalogSeeding.CreateProperty();
        return (property, Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxCapacity,
            100));
    }

    private async Task<Unit> SeedUnitAsync()
    {
        (Property property, Unit unit) = CreateTestUnit();
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // Owner first - a Unit without its Property no longer resolves.
        context.Add(property);
        context.Add(unit);
        await context.SaveChangesAsync();
        return unit;
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsForSameUnitAndOverlappingRange_ExactlyOneSucceeds()
    {
        // Arrange
        Unit unit = await SeedUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        HoldAvailabilityRequest request = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2
        };

        // Act: fire concurrent hold requests for the exact same unit/range,
        // each on its own HttpClient/connection - a single shared client
        // would serialize requests onto one connection and never actually
        // race at the database.
        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/availability/holds", request, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, holdCount);
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsForPartiallyOverlappingRanges_ExactlyOneSucceeds()
    {
        // Same guarantee, but proves the exclusion constraint catches a
        // partial overlap too, not just an identical range - each request
        // targets a range shifted by one day from the last, long enough
        // that even the two furthest-apart requests still overlap by a
        // full day. Every pair overlaps, not just adjacent ones - a
        // too-short stay length once let a request coincidentally succeed
        // alongside the winner by only touching at the half-open boundary.
        Unit unit = await SeedUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(i => factory.CreateClient().PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
                {
                    UnitId = unit.Id,
                    CheckIn = today.AddDays(i),
                    CheckOut = today.AddDays(i + concurrentRequests),
                    GuestCount = 2
                }, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, holdCount);
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsForAdjacentNonOverlappingRanges_BothSucceed()
    {
        // The other two tests prove contention is resolved correctly; this
        // one proves the opposite - two ranges that only touch at the
        // [CheckIn, CheckOut) boundary are never treated as conflicting,
        // even while the exclusion constraint is genuinely evaluating
        // concurrent, not-yet-committed inserts against each other. A
        // sequential version of this assertion wouldn't exercise anything
        // the constraint's && operator doesn't trivially get right - the
        // race is what actually tests GIST's visibility handling under
        // real contention.
        Unit unit = await SeedUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly boundary = today.AddDays(5);

        HoldAvailabilityRequest rangeA = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = boundary,
            GuestCount = 2
        };
        HoldAvailabilityRequest rangeB = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = boundary,
            CheckOut = boundary.AddDays(5),
            GuestCount = 2
        };

        // Act: several concurrent requests for EACH range, interleaved in
        // one batch - both proves the two ranges never conflict with each
        // other, and (same as the other tests) that only one request per
        // range actually wins its own range.
        const int concurrentRequestsPerRange = 5;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequestsPerRange)
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/availability/holds", rangeA, TestContext.Current.CancellationToken)),
            .. Enumerable.Range(0, concurrentRequestsPerRange)
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/availability/holds", rangeB, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert: exactly one winner per range - two successes total, not
        // one and not zero.
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2 * concurrentRequestsPerRange - 2, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, holdCount);
    }

    private const int Cap = 5;

    /// <summary>
    ///     A host with the hold cap turned down to <see cref="Cap"/> and every
    ///     request attributed to <paramref name="clientIp"/>.
    ///     appsettings.Testing.json sets MaxActiveHoldsPerClient absurdly high
    ///     so the shared factory never trips on ordinary traffic, so a cap test
    ///     has to turn it back down - same pattern as RateLimitingTests.
    ///     <para>
    ///         The IP override is what makes that safe. Every request through
    ///         TestServer has a null RemoteIpAddress, so without it the whole
    ///         suite shares ClientNetworkKey.Unknown and a neighbouring test's
    ///         live holds would count against this one's budget. Pinning a
    ///         unique address per test gives each its own partition, which is
    ///         also what the production key means.
    ///     </para>
    /// </summary>
    private WebApplicationFactory<Program> CappedFactoryFor(string clientIp) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<HoldCapOptions>(o => o.MaxActiveHoldsPerClient = Cap);

            // An IStartupFilter, not builder.Configure - the latter replaces
            // the application's pipeline outright rather than prepending to
            // it, which would leave this host with no endpoints at all.
            services.AddSingleton<IStartupFilter>(new SetRemoteIpAddress(IPAddress.Parse(clientIp)));
        }));

    private sealed class SetRemoteIpAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = address;
                    await nextMiddleware();
                });

                next(app);
            };
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsFromOneClientNetwork_NeverExceedTheCap()
    {
        // The cap runs as a plain COUNT-then-INSERT - unlike
        // PricingRuleConcurrencyTests' Serializable transactions, Postgres'
        // default Read Committed would happily let two concurrent
        // transactions both COUNT before either commits its INSERT, and the
        // cap would be oversold. Distinct target units mean the exclusion
        // constraint can never be why a request fails, isolating the cap
        // check. Only a real concurrent test can tell "correctly enforced"
        // apart from "happens to look correct because nothing has raced it".
        Unit[] units = await Task.WhenAll(Enumerable.Range(0, Cap + 4).Select(_ => SeedUnitAsync()));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        // No cookie warm-up any more. It used to exist so every "concurrent"
        // request shared one hold-session token instead of racing to mint its
        // own - which is precisely the property that made the old cap
        // worthless. The key is now the caller's network, which is shared
        // across these clients whether they cooperate or not.
        using WebApplicationFactory<Program> cappedFactory = CappedFactoryFor("198.51.100.10");

        Task<HttpResponseMessage>[] tasks =
        [
            .. units.Select(unit => cappedFactory.CreateClient().PostAsJsonAsync(
                "/api/availability/holds",
                new HoldAvailabilityRequest
                {
                    UnitId = unit.Id,
                    CheckIn = today,
                    CheckOut = today.AddDays(2),
                    GuestCount = 2
                },
                TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        int totalSucceeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        int totalRejected = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(units.Length, totalSucceeded + totalRejected);
        Assert.True(totalSucceeded <= Cap,
            $"Hold cap of {Cap} was oversold under real concurrency: {totalSucceeded} holds actually succeeded.");

        // Cross-checked against the database, not just HTTP status codes -
        // the actual invariant this cap exists to protect.
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        int actualActiveHoldCount = await context.UnitAvailabilityHolds
            .CountAsync(h => units.Select(u => u.Id).Contains(h.UnitId), TestContext.Current.CancellationToken);
        Assert.True(actualActiveHoldCount <= Cap,
            $"Hold cap of {Cap} was oversold under real concurrency: {actualActiveHoldCount} rows actually persisted.");
    }

    [Fact]
    public async Task Hold_DiscardingTheHoldSessionCookie_DoesNotGrantAFreshBudget()
    {
        // The reason the cap moved off the hold-session cookie. That cookie
        // is whatever the caller sends: delete it, get a new one, get five
        // more holds, repeat - which made a "cap" that a scripted caller
        // never encountered, while holds block real inventory through the
        // exclusion constraint.
        //
        // A brand new HttpClient per request is exactly that attack: each has
        // its own cookie jar, so each mints a fresh hold-session token. Under
        // the old per-session cap every one of these succeeds. They now share
        // a client network, so the cap applies across all of them.
        Unit[] units = await Task.WhenAll(Enumerable.Range(0, Cap + 1).Select(_ => SeedUnitAsync()));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        using WebApplicationFactory<Program> cappedFactory = CappedFactoryFor("198.51.100.20");

        // Sequential, not concurrent - concurrency is the test above's job.
        // Here each request must be able to see every previous one's hold, so
        // the only thing being measured is whether a fresh cookie resets the
        // budget.
        List<HttpStatusCode> statuses = [];
        foreach (Unit unit in units)
        {
            using HttpClient freshCookieJar = cappedFactory.CreateClient();
            HttpResponseMessage response = await freshCookieJar.PostAsJsonAsync(
                "/api/availability/holds",
                new HoldAvailabilityRequest
                {
                    UnitId = unit.Id,
                    CheckIn = today,
                    CheckOut = today.AddDays(2),
                    GuestCount = 2
                },
                TestContext.Current.CancellationToken);

            statuses.Add(response.StatusCode);
        }

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.OK, Cap), statuses.Take(Cap));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[Cap]);
    }
}
