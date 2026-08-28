using Promotions.Entities;
namespace Promotions.Features;

// Shared by ListMyPromotionsHandler and GetHostPromotionsHandler as a plain
// method call, not through the Mediator dispatch layer - same convention
// as Catalog's PropertySummaryMapper (see docs/adr/0007 for why those stay
// separate request/handler pairs despite both mapping this same shape).
internal static class PromotionSummaryMapper
{
    public static List<PromotionSummary> Map(IReadOnlyCollection<Promotion> promotions)
    {
        // Materialize first, project after - see docs/adr/0006.
        return
        [
            .. promotions.Select(p => new PromotionSummary
            {
                Id = p.Id,
                Code = p.Code,
                DiscountType = p.DiscountType,
                DiscountValue = p.DiscountValue,
                Currency = p.Currency,
                HostId = p.HostId,
                ExpiresAt = p.ExpiresAt,
                MaxRedemptions = p.MaxRedemptions,
                RedemptionCount = p.RedemptionCount
            })
        ];
    }
}
