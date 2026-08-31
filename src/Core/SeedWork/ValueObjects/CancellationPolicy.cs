using Ardalis.GuardClauses;
using System.Collections.ObjectModel;
namespace SeedWork.ValueObjects;

// A ladder of CancellationTier rungs, not a preset enum - every real-world
// shape (flexible, non-refundable, graduated) is just data here, not a
// hardcoded case needing a new enum member. One current value per Unit,
// replaced wholesale on update - not host-authored co-existing rules like
// PricingRule, so this stays a value object rather than an aggregate root.
public sealed class CancellationPolicy : IEquatable<CancellationPolicy>
{
    private readonly ReadOnlyCollection<CancellationTier> _tiers;

    private CancellationPolicy(IEnumerable<CancellationTier> tiers)
    {
        _tiers = new ReadOnlyCollection<CancellationTier>(tiers.ToList());
    }

    public IReadOnlyList<CancellationTier> Tiers => _tiers;

    // Airbnb-"Moderate"-shaped: full refund 5+ days out, half inside that
    // window, nothing inside 24 hours. Applied to every new Unit and any
    // historical Booking predating this feature - a host who never touches
    // this shouldn't silently end up fully flexible or non-refundable.
    public static CancellationPolicy CreateDefault() =>
        Create([
            new CancellationTier(5, 100m),
            new CancellationTier(1, 50m),
            new CancellationTier(0, 0m)
        ]);

    public static CancellationPolicy Create(IReadOnlyList<CancellationTier> tiers)
    {
        Guard.Against.NullOrEmpty(tiers);

        foreach (CancellationTier tier in tiers)
        {
            Guard.Against.Negative(tier.MinDaysBeforeCheckIn, nameof(tier.MinDaysBeforeCheckIn));
            Guard.Against.OutOfRange(tier.RefundPercent, nameof(tier.RefundPercent), 0m, 100m);
        }

        if (tiers.Select(t => t.MinDaysBeforeCheckIn).Distinct().Count() != tiers.Count)
        {
            throw new ArgumentException("Tier thresholds (MinDaysBeforeCheckIn) must be distinct.", nameof(tiers));
        }

        if (tiers.Count(t => t.MinDaysBeforeCheckIn == 0) != 1)
        {
            throw new ArgumentException(
                "A policy must include exactly one tier with MinDaysBeforeCheckIn = 0, so every possible " +
                "cancellation date resolves to a refund percentage.", nameof(tiers));
        }

        List<CancellationTier> descending = tiers.OrderByDescending(t => t.MinDaysBeforeCheckIn).ToList();
        for (int i = 1; i < descending.Count; i++)
        {
            if (descending[i].RefundPercent > descending[i - 1].RefundPercent)
            {
                throw new ArgumentException(
                    "Refund percent must not increase as a tier's threshold gets closer to check-in.", nameof(tiers));
            }
        }

        return new CancellationPolicy(descending);
    }

    public static CancellationPolicy Restore(IReadOnlyList<CancellationTier> tiers) => new CancellationPolicy(tiers);

    // daysBeforeCheckIn is the caller's job to floor at 0 - this only
    // guards against a negative value, it doesn't reinterpret one.
    public decimal ResolveRefundPercent(int daysBeforeCheckIn)
    {
        Guard.Against.Negative(daysBeforeCheckIn);

        // Not assuming stored order - Create sorts descending, but Restore
        // (jsonb deserialization order) doesn't. The applicable tier is the
        // largest MinDaysBeforeCheckIn that still qualifies.
        return _tiers
            .Where(t => t.MinDaysBeforeCheckIn <= daysBeforeCheckIn)
            .OrderByDescending(t => t.MinDaysBeforeCheckIn)
            .First()
            .RefundPercent;
    }

    public bool Equals(CancellationPolicy? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _tiers.SequenceEqual(other._tiers);
    }

    public override bool Equals(object? obj) => Equals(obj as CancellationPolicy);

    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        foreach (CancellationTier tier in _tiers)
        {
            hash.Add(tier);
        }
        return hash.ToHashCode();
    }
}
