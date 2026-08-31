using Availability;
using Availability.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private static Unit CreateTestUnit(int maxCapacity = 10)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxCapacity,
            100);
    }

    private async Task<Unit> SeedUnitAsync()
    {
        Unit unit = CreateTestUnit();
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
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

    [Fact]
    public async Task Hold_ConcurrentRequestsSharingTheSameHolderToken_NeverExceedTheSessionCap()
    {
        // The per-session cap runs as a plain COUNT-then-INSERT under
        // Postgres' default Read Committed, no explicit row locking -
        // unlike PricingRuleConcurrencyTests' Serializable transactions.
        // Distinct target units mean the exclusion constraint can never be
        // why a request fails, isolating the cap check: if Read Committed
        // lets two concurrent transactions both COUNT before either
        // commits its INSERT, the cap can be oversold. Only a real
        // concurrent test can tell "correctly enforced" apart from
        // "happens to look correct because nothing has ever raced it".
        const int cap = 5;
        Unit[] units = await Task.WhenAll(Enumerable.Range(0, cap + 4).Select(_ => SeedUnitAsync()));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        using HttpClient client = factory.CreateClient();

        // A single sequential warm-up request establishes the hold-session
        // cookie on this client (and consumes the first of the cap's 5
        // slots) before any concurrent request fires - otherwise every
        // "concurrent" request below would race to mint its own fresh
        // token instead of sharing one, which would test nothing at all.
        HttpResponseMessage warmUp = await client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = units[0].Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, warmUp.StatusCode);

        Task<HttpResponseMessage>[] tasks =
        [
            .. units.Skip(1).Select(unit => client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = today,
                CheckOut = today.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        int totalSucceeded = 1 + responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        int totalRejected = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(units.Length, totalSucceeded + totalRejected);
        Assert.True(totalSucceeded <= cap,
            $"Session cap of {cap} was oversold under real concurrency: {totalSucceeded} holds actually succeeded.");

        // Cross-checked against the database, not just HTTP status codes -
        // the actual invariant this cap exists to protect.
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        int actualActiveHoldCount = await context.UnitAvailabilityHolds
            .CountAsync(h => units.Select(u => u.Id).Contains(h.UnitId), TestContext.Current.CancellationToken);
        Assert.True(actualActiveHoldCount <= cap,
            $"Session cap of {cap} was oversold under real concurrency: {actualActiveHoldCount} rows actually persisted.");
    }
}
