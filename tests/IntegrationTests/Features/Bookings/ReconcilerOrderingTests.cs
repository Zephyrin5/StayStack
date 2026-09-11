using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Jobs;
using Bookings.Outbox;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using Promotions;
using Promotions.Contracts;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Bookings;

// A reconciler must not commit a cross-module compensation before the local
// decision that authorises it. If it does, a rollback restores the intent and
// the hold, the original request resumes and succeeds, and it writes a booking
// whose discount has already been clawed back.
//
// Running the reconciler twice would not catch this: the second run is a
// harmless no-op against an already-reversed redemption. That protection only
// holds while nothing else can succeed in between, and the original request
// can - which is the whole property under test.
[Collection("Integration Tests")]
public class ReconcilerOrderingTests(IntegrationTestWebApplicationFactory factory)
{
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

    // Fails the reconciler's own SaveChanges, after everything inside its
    // transaction has run. Whatever the reconciler committed elsewhere by then
    // is already gone from this app's control.
    private sealed class FailingBookingsDbContext : AppBookingsDbContext
    {
        public FailingBookingsDbContext(DbContextOptions<AppBookingsDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated failure of the reconciler's own transaction");
    }

    [Fact]
    public async Task AReconcilerWhoseTransactionFails_LeavesTheRedemptionIntact()
    {
        Unit unit = CreateTestUnit();
        Guid bookingId = Guid.CreateVersion7();
        Guid holdId = Guid.CreateVersion7();
        Guid promotionId = Guid.CreateVersion7();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(45);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(_pendingProperties);
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

            // A redemption that the slow confirmation has already taken - the
            // thing the reconciler must not reverse ahead of its own commit.
            AppPromotionsDbContext promotions = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
            Promotion promotion = Promotion.CreatePlatformPromotion(
                $"RECON{promotionId:N}"[..12], PromotionDiscountType.Percentage, 10m,
                currency: null, expiresAt: DateTimeOffset.UtcNow.AddDays(30), maxRedemptions: null, hostId: null);
            promotions.Promotions.Add(promotion);
            await promotions.SaveChangesAsync(TestContext.Current.CancellationToken);

            IPromotionRedemption redemption = scope.ServiceProvider.GetRequiredService<IPromotionRedemption>();
            await redemption.RedeemAsync(
                promotion.Code, unit.Id, "jane@example.com", Money.Of(200m, Currency.KWD), bookingId,
                TestContext.Current.CancellationToken);

            AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookings.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
            {
                Id = holdId,
                UnitId = unit.Id,
                StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
                Status = "pending_payment",
                GuestCount = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                TotalPrice = Money.Of(200m, Currency.KWD),
                Subtotal = 200m
            });
            bookings.PendingBookingIntents.Add(new PendingBookingIntent
            {
                Id = bookingId,
                HoldId = holdId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
            });
            await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act - the reconciler claims the intent and then fails its own
        // transaction. It swallows the per-row failure and moves on.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            FakeTimeProvider timeProvider = new FakeTimeProvider();
            timeProvider.SetUtcNow(DateTimeOffset.UtcNow);

            using FailingBookingsDbContext failing = new FailingBookingsDbContext(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<AppBookingsDbContext>>());

            ReconcileOrphanedBookingIntentsJob job = new ReconcileOrphanedBookingIntentsJob(
                failing,
                scope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
                timeProvider,
                NullLogger<ReconcileOrphanedBookingIntentsJob>.Instance);

            await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
        }

        // Assert - the redemption the original request still depends on is
        // untouched. Reversed ahead of the commit, it would be gone, and the
        // resuming request would write a booking whose discount had already
        // been clawed back.
        using IServiceScope assertScope = factory.Services.CreateScope();
        AppPromotionsDbContext promotionsDb = assertScope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();

        // ReversedAt, not row existence: ReverseRedemptionAsync is an UPDATE
        // setting reversed_at, never a DELETE, so a test asserting the row is
        // still there passes just as happily against a fully reversed
        // redemption. It did, until this was checked.
        PromotionRedemption redemptionRow = await promotionsDb.PromotionRedemptions.AsNoTracking()
            .SingleAsync(r => r.BookingId == bookingId, TestContext.Current.CancellationToken);

        Assert.Null(redemptionRow.ReversedAt);

        // And the intent survived the rollback, so the original request - or a
        // later reconcile - still has something to act on.
        AppBookingsDbContext bookingsDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Assert.True(await bookingsDb.PendingBookingIntents.AsNoTracking()
            .AnyAsync(i => i.Id == bookingId, TestContext.Current.CancellationToken));
    }
}
