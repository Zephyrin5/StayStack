using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

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
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/catalog/holds", request, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, holdCount);
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsForPartiallyOverlappingRanges_ExactlyOneSucceeds()
    {
        // Same guarantee, but proves the exclusion constraint catches a
        // partial overlap too, not just an identical range - each request
        // targets a range shifted by one day from the last. The stay
        // length (concurrentRequests nights) is deliberately long enough
        // that even the two furthest-apart requests (i=0 and i=9, shifted
        // 9 days apart) still overlap by a full day - every pair overlaps,
        // not just adjacent ones, so nothing here can coincidentally
        // succeed alongside the winner the way a too-short stay length did
        // when this test was first written (three requests spaced exactly
        // 3 days apart with a 3-night stay don't actually overlap at all,
        // touching only at the half-open boundary).
        Unit unit = await SeedUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(i => factory.CreateClient().PostAsJsonAsync("/api/catalog/holds", new HoldAvailabilityRequest
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
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, holdCount);
    }

    [Fact]
    public async Task Hold_ConcurrentRequestsForAdjacentNonOverlappingRanges_BothSucceed()
    {
        // The other two tests prove contention is resolved correctly; this
        // one proves the opposite - that two ranges which only touch at
        // the [CheckIn, CheckOut) boundary (see HoldAvailabilityHandler's
        // own half-open comment) are never treated as conflicting, even
        // when the exclusion constraint is genuinely evaluating concurrent,
        // not-yet-committed inserts against each other at the database
        // level. A single-threaded/sequential version of this assertion
        // wouldn't exercise anything the constraint's own && operator
        // doesn't already trivially get right - the concurrent race is
        // what actually tests whether GIST's own visibility handling holds
        // up under real contention, not just the operator's boundary math.
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
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/catalog/holds", rangeA, TestContext.Current.CancellationToken)),
            .. Enumerable.Range(0, concurrentRequestsPerRange)
                .Select(_ => factory.CreateClient().PostAsJsonAsync("/api/catalog/holds", rangeB, TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert: exactly one winner per range - two successes total, not
        // one and not zero.
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2 * concurrentRequestsPerRange - 2, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        int holdCount = await context.UnitAvailabilityHolds.CountAsync(h => h.UnitId == unit.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, holdCount);
    }
}
