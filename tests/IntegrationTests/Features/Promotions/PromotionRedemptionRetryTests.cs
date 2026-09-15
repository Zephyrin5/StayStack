// Proves a redemption whose commit loses its acknowledgement (FailAfterCommit) returns the
// redemption it made. With the id minted inside the retried delegate, the retry is rejected as
// "already used by this email address".
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promotions;
using Promotions.Contracts;
using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Promotions;

// The sixth instance of docs/adr/0025's identity rule, and the first one found by
// RetryIdentityProtocolTests rather than by hand.
//
// RedeemAsync minted the redemption id inside its retried delegate. After a
// commit lost its acknowledgement, the retry held a new id, re-ran the cap
// increment, and its insert met the first attempt's committed row on the
// one-redemption-per-email index - so the guest's own redemption came back as
// "already used by this email address", and the checkout it belonged to failed.
[Collection("Integration Tests")]
public class PromotionRedemptionRetryTests(IntegrationTestWebApplicationFactory factory)
{
    [Fact]
    public async Task ARedemptionWhoseCommitLosesItsAcknowledgement_ReturnsTheRedemptionItAlreadyMade()
    {
        Promotion promotion = Promotion.CreatePlatformPromotion(
            Guid.CreateVersion7(),
            $"RETRY{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            PromotionDiscountType.Percentage,
            10m,
            null,
            expiresAt: null,
            maxRedemptions: null,
            hostId: null);

        using (IServiceScope seed = factory.Services.CreateScope())
        {
            AppPromotionsDbContext context = seed.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
            context.Add(promotion);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Guid bookingId = Guid.CreateVersion7();

        // After the commit that made a redemption for this booking durable. The
        // insert is Dapper, so it is recognised by the committed row rather than
        // by the change tracker.
        CommitFault<AppPromotionsDbContext> lostAck = CommitFaults.FailAfterCommit<AppPromotionsDbContext>(
            (context, ct) => CommitFaults.CommittedRowExistsAsync(
                context, "SELECT 1 FROM promotion_redemptions WHERE booking_id = @BookingId", "BookingId", bookingId, ct));

        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        PromotionRedemptionResult result;

        using (IServiceScope scope = host.Services.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IPromotionRedemption>().RedeemAsync(
                promotion.Code, Guid.NewGuid(), "guest@example.com",
                Money.Of(200m, Currency.KWD), bookingId, TestContext.Current.CancellationToken);
        }

        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the redemption.");

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppPromotionsDbContext db = assertScope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();

        // One redemption, the one returned, and one slot counted - a retry that
        // re-ran the cap increment and kept it would burn a second.
        List<global::Promotions.Entities.PromotionRedemption> redemptions = await db.PromotionRedemptions.AsNoTracking()
            .Where(r => r.BookingId == bookingId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(result.RedemptionId, Assert.Single(redemptions).Id);

        Promotion persisted = await db.Promotions.AsNoTracking()
            .SingleAsync(p => p.Id == promotion.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, persisted.RedemptionCount);
    }
}
