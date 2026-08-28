using Mediator;
using Promotions.Enums;
using SeedWork.Enums;
namespace Promotions.Features.CreatePromotion;

// No HostId here, deliberately - same reasoning as CreatePropertyRequest.
// This endpoint is Host-only; HostId is derived server-side from the
// caller's token (see CreatePromotionHandler). Admins creating a
// host-scoped or platform-wide code go through AdminCreatePromotion
// instead, which takes HostId as an explicit (optional) body field.
public record CreatePromotionRequest : IRequest<CreatePromotionResponse>
{
    public required string Code { get; init; }
    public PromotionDiscountType DiscountType { get; init; }
    public decimal DiscountValue { get; init; }

    // Required for FixedAmount only - see CreatePromotionRequestValidator.
    public Currency? Currency { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
    public int? MaxRedemptions { get; init; }
}
