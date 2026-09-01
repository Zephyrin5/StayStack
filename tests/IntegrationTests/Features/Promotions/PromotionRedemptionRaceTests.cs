using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Promotions;
using Promotions.Contracts;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Promotions;

// RedeemAsync validates a promotion from a plain snapshot read, then enforces
// the redemption cap atomically inside a transaction. Expiry and archival used
// to be checked only in that snapshot, which left a window: a code could lapse
// or be deleted between the read and the write, and still redeem.
//
// These drive that window deterministically rather than racing threads for it.
[Collection("Integration Tests")]
public class PromotionRedemptionRaceTests(IntegrationTestWebApplicationFactory factory)
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     A host whose clock advances one minute on every read. RedeemAsync
    ///     reads it once for the snapshot expiry check and again for the
    ///     atomic predicate, so an expiry falling between those two readings
    ///     reproduces the race exactly - and reproduces the honest version of
    ///     it, since a time-based expiry lapses mid-request with nobody
    ///     editing anything.
    /// </summary>
    private (WebApplicationFactory<Program> Factory, FakeTimeProvider Clock) CreateHostWithAdvancingClock()
    {
        FakeTimeProvider clock = new FakeTimeProvider();
        clock.SetUtcNow(Start);
        clock.AutoAdvanceAmount = TimeSpan.FromMinutes(1);

        WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }));

        return (host, clock);
    }

    private async Task<Promotion> SeedPromotionAsync(DateTimeOffset? expiresAt, Guid? hostId = null)
    {
        Promotion promotion = Promotion.CreatePlatformPromotion(
            $"RACE{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            PromotionDiscountType.Percentage,
            10m,
            null,
            expiresAt,
            maxRedemptions: null,
            hostId);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        context.Add(promotion);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return promotion;
    }

    [Fact]
    public async Task Redeem_WhenTheCodeExpiresBetweenTheSnapshotReadAndTheWrite_IsRejected()
    {
        // FakeTimeProvider returns the current instant and *then* advances, so
        // the two readings RedeemAsync makes are T+0 (the snapshot expiry
        // check) and T+1min (the predicate's @Now). An expiry at T+30s sits
        // between them: legal when validated, lapsed when written. That is
        // the whole race, made deterministic - and it is the honest form of
        // it, since a time-based expiry passes mid-request with nobody
        // editing anything.
        Promotion promotion = await SeedPromotionAsync(expiresAt: Start.AddSeconds(30));

        (WebApplicationFactory<Program> host, _) = CreateHostWithAdvancingClock();
        using WebApplicationFactory<Program> _host = host;

        using IServiceScope scope = host.Services.CreateScope();
        IPromotionRedemption redemption = scope.ServiceProvider.GetRequiredService<IPromotionRedemption>();

        PromotionInvalidException exception = await Assert.ThrowsAsync<PromotionInvalidException>(() =>
            redemption.RedeemAsync(
                promotion.Code, Guid.NewGuid(), "guest@example.com",
                Money.Of(200m, Currency.KWD), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Contains("has expired", exception.Message);

        // The count must not have moved. A redemption rejected by the
        // predicate has to leave no trace - if the UPDATE had matched and the
        // rejection came later, the slot would be burned for nothing.
        await AssertRedemptionCountAsync(promotion.Id, expected: 0);
    }

    [Fact]
    public async Task Redeem_WhenTheCodeIsArchivedAfterTheSnapshotRead_IsRejected()
    {
        // The archival half of the same window, driven through a real seam in
        // the production call order rather than simulated: for a host-scoped
        // promotion, RedeemAsync calls IUnitLookup.GetUnitAsync to check
        // ownership *after* its snapshot read and *before* it opens the
        // transaction. Archiving the promotion from inside that call is
        // exactly "a host deletes the code while a guest is checking out",
        // with the interleaving pinned instead of raced for.
        //
        // Under the previous predicate this redeemed successfully: the
        // snapshot had already seen a live row, and the UPDATE had no status
        // clause to notice otherwise.
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = CatalogSeeding.CreateUnit(property);
        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = seedScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.Add(property);
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Promotion promotion = await SeedPromotionAsync(expiresAt: null, hostId: property.HostId);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IUnitLookup));
                services.Remove(original);
                services.Add(new ServiceDescriptor(
                    typeof(IUnitLookup),
                    sp => new ArchiveOnLookup(
                        (IUnitLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                        () => ArchivePromotionAsync(promotion.Id)),
                    original.Lifetime));
            }));

        using IServiceScope scope = host.Services.CreateScope();
        IPromotionRedemption redemption = scope.ServiceProvider.GetRequiredService<IPromotionRedemption>();

        PromotionInvalidException exception = await Assert.ThrowsAsync<PromotionInvalidException>(() =>
            redemption.RedeemAsync(
                promotion.Code, unit.Id, "guest@example.com",
                Money.Of(200m, Currency.KWD), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        // Reports "does not exist", matching what the snapshot read says for
        // an archived code - a caller should not be able to tell an archived
        // promotion apart from one that never existed.
        Assert.Contains("does not exist", exception.Message);

        await AssertRedemptionCountAsync(promotion.Id, expected: 0);
    }

    private async Task ArchivePromotionAsync(Guid promotionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await context.Promotions.IgnoreQueryFilters().SingleAsync(p => p.Id == promotionId);
        promotion.Archive(Start, null);
        await context.SaveChangesAsync();
    }

    // Runs a side effect on the ownership lookup, then delegates. Only
    // GetUnitAsync is interposed; everything else passes straight through, so
    // this changes when the promotion is archived and nothing else.
    private sealed class ArchiveOnLookup(IUnitLookup inner, Func<Task> onLookup) : IUnitLookup
    {
        public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
        {
            await onLookup();
            return await inner.GetUnitAsync(unitId, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(
            IEnumerable<Guid> unitIds, CancellationToken cancellationToken) =>
            inner.GetUnitsAsync(unitIds, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
            inner.GetUnitIdsForHostAsync(hostId, cancellationToken);

        public Task<StayPricingResult?> ResolveStayPricingAsync(
            Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken) =>
            inner.ResolveStayPricingAsync(unitId, checkIn, checkOut, cancellationToken);
    }

    private async Task AssertRedemptionCountAsync(Guid promotionId, int expected)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await context.Promotions
            .IgnoreQueryFilters()
            .SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);

        Assert.Equal(expected, promotion.RedemptionCount);
    }
}
