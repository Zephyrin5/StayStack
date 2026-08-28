using Ardalis.GuardClauses;
using Promotions.Enums;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.Interfaces;
namespace Promotions.Entities;

// RedemptionCount is mutated via raw SQL on the hot concurrent-redemption
// path (see Promotions.Contracts' PromotionRedemption implementation), not
// through a business method on this entity or EF change tracking - a
// tracked increment can't provide the "don't let two concurrent redemptions
// both pass the cap check" atomicity that path needs. Everything else about
// this entity (create/update/archive) is low-frequency admin action and
// goes through EF normally.
public sealed class Promotion : Entity, IAggregateRoot
{
    private Promotion(
        Guid id,
        string code,
        PromotionDiscountType discountType,
        decimal discountValue,
        Currency? currency,
        Guid? hostId,
        DateTimeOffset? expiresAt,
        int? maxRedemptions)
    {
        Id = id;
        Code = code;
        DiscountType = discountType;
        DiscountValue = discountValue;
        Currency = currency;
        HostId = hostId;
        ExpiresAt = expiresAt;
        MaxRedemptions = maxRedemptions;
        RedemptionCount = 0;
    }

    public string Code { get; private set; } = string.Empty;
    public PromotionDiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }

    // FixedAmount only - a Percentage discount is currency-agnostic.
    public Currency? Currency { get; private set; }

    // null = platform-wide, redeemable against any host's units.
    public Guid? HostId { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }
    public int? MaxRedemptions { get; private set; }
    public int RedemptionCount { get; private set; }

    public static Promotion CreateHostPromotion(
        Guid hostId,
        string code,
        PromotionDiscountType discountType,
        decimal discountValue,
        Currency? currency,
        DateTimeOffset? expiresAt,
        int? maxRedemptions)
    {
        Guard.Against.Default(hostId);
        return Create(code, discountType, discountValue, currency, hostId, expiresAt, maxRedemptions);
    }

    public static Promotion CreatePlatformPromotion(
        string code,
        PromotionDiscountType discountType,
        decimal discountValue,
        Currency? currency,
        DateTimeOffset? expiresAt,
        int? maxRedemptions,
        Guid? hostId)
    {
        return Create(code, discountType, discountValue, currency, hostId, expiresAt, maxRedemptions);
    }

    // Code and DiscountType are immutable after creation - no setters for
    // either, same "can't change the discriminator" rule PricingRule's
    // RuleType already enforces by omission.

    public void SetDiscountValue(decimal discountValue)
    {
        ValidateDiscountValue(DiscountType, discountValue);
        DiscountValue = discountValue;
    }

    public void SetCurrency(Currency? currency)
    {
        ValidateCurrency(DiscountType, currency);
        Currency = currency;
    }

    public void SetExpiresAt(DateTimeOffset? expiresAt) => ExpiresAt = expiresAt;

    public void SetMaxRedemptions(int? maxRedemptions)
    {
        if (maxRedemptions is not null)
        {
            Guard.Against.NegativeOrZero(maxRedemptions.Value, nameof(maxRedemptions));
        }

        MaxRedemptions = maxRedemptions;
    }

    private static Promotion Create(
        string code,
        PromotionDiscountType discountType,
        decimal discountValue,
        Currency? currency,
        Guid? hostId,
        DateTimeOffset? expiresAt,
        int? maxRedemptions)
    {
        Guard.Against.NullOrWhiteSpace(code);
        string normalizedCode = code.Trim().ToUpperInvariant();
        ValidateDiscountValue(discountType, discountValue);
        ValidateCurrency(discountType, currency);
        if (maxRedemptions is not null)
        {
            Guard.Against.NegativeOrZero(maxRedemptions.Value, nameof(maxRedemptions));
        }

        return new Promotion(
            Guid.CreateVersion7(), normalizedCode, discountType, discountValue, currency,
            hostId, expiresAt, maxRedemptions);
    }

    private static void ValidateDiscountValue(PromotionDiscountType discountType, decimal discountValue)
    {
        if (discountType == PromotionDiscountType.Percentage)
        {
            Guard.Against.OutOfRange(discountValue, nameof(discountValue), 0.01m, 100m);
        }
        else
        {
            Guard.Against.NegativeOrZero(discountValue);
        }
    }

    private static void ValidateCurrency(PromotionDiscountType discountType, Currency? currency)
    {
        if (discountType == PromotionDiscountType.FixedAmount && currency is null)
        {
            throw new ArgumentException("Currency is required for a fixed-amount discount.", nameof(currency));
        }
    }
}
