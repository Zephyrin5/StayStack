using Bookings;
using Bookings.Entities;
using Bookings.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Promotions;
using Promotions.Contracts;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Bookings;

// The redemption-side counterpart to ReconcileOrphanedBookedHoldsJobTests -
// proves the backstop for a RedeemAsync commit (Promotions' own database)
// that succeeds before Bookings ever gets a chance to create the Booking or
// enqueue a compensation. See
// Bookings.Jobs.ReconcileOrphanedPromotionRedemptionsJob's own doc comment.
[Collection("Integration Tests")]
public class ReconcileOrphanedPromotionRedemptionsJobTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task<Promotion> SeedRedeemedPromotionAsync()
    {
        Promotion promotion = Promotion.CreatePlatformPromotion(
            $"TEST{Guid.NewGuid():N}"[..12], PromotionDiscountType.Percentage, 10m, null, null, null, null);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        context.Add(promotion);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // RedemptionCount is only ever mutated via the raw SQL RedeemAsync
        // itself uses (see Promotion's own doc comment) - simulated here to
        // put the promotion in the state a real redemption would have left
        // it, so the job's reversal has something real to decrement back.
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE promotions SET redemption_count = 1 WHERE id = {0}", promotion.Id);

        return promotion;
    }

    private async Task SeedActiveRedemptionAsync(Guid promotionId, Guid bookingId, DateTimeOffset redeemedAt)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        context.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.CreateVersion7(),
            PromotionId = promotionId,
            BookingId = bookingId,
            GuestEmail = "jane@example.com",
            DiscountAmount = Money.Of(20m, Currency.KWD),
            RedeemedAt = redeemedAt
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedBookingAsync(Guid bookingId, bool cancelled = false)
    {
        Booking booking = Booking.Create(
            bookingId, Guid.NewGuid(), Guid.NewGuid(), null,
            "Jane Guest", "jane@example.com", null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            2, Money.Of(200m, Currency.KWD), 200m, CancellationPolicy.CreateDefault());
        if (cancelled)
        {
            booking.Cancel();
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        context.Bookings.Add(booking);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PromotionRedemption> GetRedemptionAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        return await context.PromotionRedemptions.AsNoTracking()
            .SingleAsync(r => r.BookingId == bookingId, TestContext.Current.CancellationToken);
    }

    private async Task<int> GetRedemptionCountAsync(Guid promotionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext context = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await context.Promotions.AsNoTracking()
            .SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);
        return promotion.RedemptionCount;
    }

    private async Task RunJobAsync(DateTimeOffset now)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        IRedemptionLookup redemptionLookup = scope.ServiceProvider.GetRequiredService<IRedemptionLookup>();
        IPromotionRedemption promotionRedemption = scope.ServiceProvider.GetRequiredService<IPromotionRedemption>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        ILogger<ReconcileOrphanedPromotionRedemptionsJob> logger =
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedPromotionRedemptionsJob>>();
        ReconcileOrphanedPromotionRedemptionsJob job = new ReconcileOrphanedPromotionRedemptionsJob(
            bookingsDb, redemptionLookup, promotionRedemption, timeProvider, logger);
        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReconcileAsync_ReversesOrphanedRedemption_WithNoMatchingBooking()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Promotion promotion = await SeedRedeemedPromotionAsync();
        Guid bookingId = Guid.NewGuid();
        await SeedActiveRedemptionAsync(promotion.Id, bookingId, redeemedAt: now.AddMinutes(-20));

        await RunJobAsync(now);

        PromotionRedemption redemption = await GetRedemptionAsync(bookingId);
        Assert.NotNull(redemption.ReversedAt);
        Assert.Equal(0, await GetRedemptionCountAsync(promotion.Id));
    }

    [Fact]
    public async Task ReconcileAsync_ReversesOrphanedRedemption_BehindACancelledBooking()
    {
        // The second orphan shape docs/adr/0003 describes: the booking row
        // exists, it's just Cancelled with a redemption still active behind
        // it - a dead-lettered ReverseRedemptionOutboxMessage the sweep
        // hasn't yet resolved.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Promotion promotion = await SeedRedeemedPromotionAsync();
        Guid bookingId = Guid.NewGuid();
        await SeedActiveRedemptionAsync(promotion.Id, bookingId, redeemedAt: now.AddMinutes(-20));
        await SeedBookingAsync(bookingId, cancelled: true);

        await RunJobAsync(now);

        PromotionRedemption redemption = await GetRedemptionAsync(bookingId);
        Assert.NotNull(redemption.ReversedAt);
        Assert.Equal(0, await GetRedemptionCountAsync(promotion.Id));
    }

    [Fact]
    public async Task ReconcileAsync_LeavesGenuinelyActiveRedemption_WithLiveBooking_Untouched()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Promotion promotion = await SeedRedeemedPromotionAsync();
        Guid bookingId = Guid.NewGuid();
        await SeedActiveRedemptionAsync(promotion.Id, bookingId, redeemedAt: now.AddMinutes(-20));
        await SeedBookingAsync(bookingId);

        await RunJobAsync(now);

        PromotionRedemption redemption = await GetRedemptionAsync(bookingId);
        Assert.Null(redemption.ReversedAt);
        Assert.Equal(1, await GetRedemptionCountAsync(promotion.Id));
    }

    [Fact]
    public async Task ReconcileAsync_LeavesRecentlyRedeemed_WithinGraceWindow_Untouched()
    {
        // Redeemed 2 minutes ago, well inside the 10-minute grace window -
        // this is what a redemption looks like mid-flight between
        // RedeemAsync's commit and the Booking insert that normally follows
        // it within milliseconds. Must not be reversed just because the
        // Booking row hasn't landed yet.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Promotion promotion = await SeedRedeemedPromotionAsync();
        Guid bookingId = Guid.NewGuid();
        await SeedActiveRedemptionAsync(promotion.Id, bookingId, redeemedAt: now.AddMinutes(-2));

        await RunJobAsync(now);

        PromotionRedemption redemption = await GetRedemptionAsync(bookingId);
        Assert.Null(redemption.ReversedAt);
        Assert.Equal(1, await GetRedemptionCountAsync(promotion.Id));
    }
}
