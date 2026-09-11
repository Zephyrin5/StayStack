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
using Bookings.Features.ConfirmBooking;
using System.Net;
using System.Net.Http.Json;
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

    // Holds RedeemAsync open until the test says go, so the redemption commits
    // *after* the reconciler has already been and gone. Everything else passes
    // straight through - this is the one call whose commit point matters.
    private sealed class RedeemOnCue(IPromotionRedemption inner, TaskCompletionSource gate, TaskCompletionSource reached)
        : IPromotionRedemption
    {
        public async Task<PromotionRedemptionResult> RedeemAsync(
            string code, Guid unitId, string guestEmail, Money subtotal, Guid bookingId,
            CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await gate.Task;
            return await inner.RedeemAsync(code, unitId, guestEmail, subtotal, bookingId, cancellationToken);
        }

        public Task ReverseRedemptionAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.ReverseRedemptionAsync(bookingId, cancellationToken);
    }

    [Fact]
    public async Task AConfirmationReconciledWhileItsRedemptionWasStillCommitting_DoesNotBurnTheCode()
    {
        // The mirror image of the test above, and the one the outbox rewrite
        // did not fix. There the reconciler reversed too early and the request
        // succeeded; here the reconciler reverses too early against a
        // redemption that does not exist yet, so the reversal no-ops and is
        // marked processed - and then the request's RedeemAsync commits.
        //
        //   reconciler:   releases the hold, deletes the intent, commits
        //                 dispatches its reversal -> nothing to reverse -> no-op
        //   this request: RedeemAsync commits -> the promotion is consumed
        //                 the tracked intent delete affects 0 rows
        //                   -> DbUpdateConcurrencyException
        //
        // That branch used to compensate nothing, on the reasoning that the
        // reconciler had already done it. End state: no booking, and a code
        // burned for good.
        //
        // Running the reconciler twice proves nothing about this. Its second
        // run is a no-op, and the no-op is the defect.
        Unit unit = CreateTestUnit();
        Guid holdId = Guid.CreateVersion7();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(52);
        string promoCode = $"BURN{Guid.CreateVersion7():N}"[..12];
        Guid promotionId;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(_pendingProperties);
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);

            AppPromotionsDbContext promotions = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
            Promotion seeded = Promotion.CreatePlatformPromotion(
                promoCode, PromotionDiscountType.Percentage, 10m,
                currency: null, expiresAt: DateTimeOffset.UtcNow.AddDays(30), maxRedemptions: null, hostId: null);
            promotions.Promotions.Add(seeded);
            await promotions.SaveChangesAsync(TestContext.Current.CancellationToken);
            promotionId = seeded.Id;

            // A real claimable hold: ConfirmHoldAsync matches status = 'held'
            // AND hold_expires_at > now, so the expiry has to be in the future
            // or the handler never gets as far as the redemption.
            AppBookingsDbContext bookings = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookings.UnitAvailabilityHolds.Add(new UnitAvailabilityHold
            {
                Id = holdId,
                UnitId = unit.Id,
                StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
                Status = "held",
                GuestCount = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                HoldExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                TotalPrice = Money.Of(200m, Currency.KWD),
                Subtotal = 200m
            });
            await bookings.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reachedRedeem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HttpClient client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IPromotionRedemption));
                services.Remove(original);
                services.AddScoped<IPromotionRedemption>(sp => new RedeemOnCue(
                    (IPromotionRedemption)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                    gate, reachedRedeem));
            })).CreateClient();

        // Act - the confirmation runs as far as RedeemAsync and stops there,
        // with its intent already committed.
        Task<HttpResponseMessage> confirmation = client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com",
            PromoCode = promoCode
        }, TestContext.Current.CancellationToken);

        await reachedRedeem.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The reconciler, run against a clock far enough forward that this
        // intent is past its grace period. Everything it does commits while
        // the request is still parked inside RedeemAsync.
        using (IServiceScope reconcileScope = factory.Services.CreateScope())
        {
            FakeTimeProvider timeProvider = new FakeTimeProvider();
            timeProvider.SetUtcNow(DateTimeOffset.UtcNow + PendingBookingIntent.ReconcileGrace + TimeSpan.FromMinutes(1));

            ReconcileOrphanedBookingIntentsJob job = new ReconcileOrphanedBookingIntentsJob(
                reconcileScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                reconcileScope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                reconcileScope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
                timeProvider,
                NullLogger<ReconcileOrphanedBookingIntentsJob>.Instance);

            await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
        }

        // Now let the redemption land, behind the reversal that was supposed
        // to cover it.
        gate.SetResult();

        HttpResponseMessage response = await confirmation;

        // Assert - the request is correctly refused. That part always worked;
        // it is what it left behind that did not.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppPromotionsDbContext promotionsDb = assertScope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();

        Promotion promotion = await promotionsDb.Promotions.AsNoTracking()
            .SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);

        // ReversedAt rather than row existence, for the same reason the test
        // above spells out: reversal is an UPDATE, never a DELETE.
        List<PromotionRedemption> redemptions = await promotionsDb.PromotionRedemptions.AsNoTracking()
            .Where(r => r.PromotionId == promotion.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The redemption did happen - if it had not, this test would be
        // asserting nothing at all, since "no active redemption" is trivially
        // true when no redemption was ever written.
        Assert.NotEmpty(redemptions);
        Assert.All(redemptions, redemption => Assert.NotNull(redemption.ReversedAt));

        // And the counter is back where it started, which is what decides
        // whether the next guest can still use the code.
        Assert.Equal(0, promotion.RedemptionCount);
    }
}
