using Microsoft.EntityFrameworkCore;
using Promotions.Entities;
namespace Promotions.Contracts;

// internal, same reasoning as PromotionRedemption itself - Bookings should
// only ever reach this through IRedemptionLookup, resolved via DI.
internal class RedemptionLookup(AppPromotionsDbContext dbContext) : IRedemptionLookup
{
    public async Task<IReadOnlyList<Guid>> GetActiveRedemptionBookingIdsOlderThanAsync(
        DateTimeOffset cutoff, DateTimeOffset earliestRedeemedAt, int maxResults, CancellationToken cancellationToken)
    {
        return await dbContext.PromotionRedemptions
            .Where(r => r.ReversedAt == null && r.RedeemedAt > earliestRedeemedAt && r.RedeemedAt <= cutoff)
            .OrderBy(r => r.RedeemedAt)
            .Select(r => r.BookingId)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }
}
