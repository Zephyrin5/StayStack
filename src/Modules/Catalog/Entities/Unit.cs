using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.Interfaces;
using SeedWork.ValueObjects;
namespace Catalog.Entities;

public sealed class Unit : Entity, IAggregateRoot
{
    // EF can't bind a ComplexProperty (Money) parameter back to the
    // entity's own mapped complex property - see Booking's identical
    // constructor pair for the full explanation and docs/adr/0015. This is
    // EF's materialization fallback only; Create() below still goes
    // through the real constructor for every write. Name/CancellationPolicy
    // get real placeholders only to satisfy the non-nullable-reference-type
    // check - EF overwrites them immediately after construction.
    private Unit()
    {
        Name = LocalizedText.Restore(new Dictionary<string, string>());
        CancellationPolicy = CancellationPolicy.CreateDefault();
    }

    // See Property.cs for why materialization goes through a real
    // constructor rather than a parameterless one + `required`/`null!`.
    private Unit(
        Guid id,
        Guid propertyId,
        LocalizedText name,
        int maxOccupancy,
        Money basePrice,
        CancellationPolicy cancellationPolicy)
    {
        Id = id;
        PropertyId = propertyId;
        Name = name;
        MaxOccupancy = maxOccupancy;
        BasePrice = basePrice;
        CancellationPolicy = cancellationPolicy;
    }
    public Guid PropertyId { get; private set; }
    public LocalizedText Name { get; private set; }
    public int MaxOccupancy { get; private set; }
    public Money BasePrice { get; private set; }

    // One current value, like BasePrice/Currency - not a variable set of
    // co-existing host-authored rows the way PricingRule is, so it's
    // replaced wholesale via SetCancellationPolicy rather than a separate
    // create/delete-able sub-resource.
    public CancellationPolicy CancellationPolicy { get; private set; }

    public static Unit Create(
        Guid propertyId,
        LocalizedText name,
        int maxOccupancy,
        decimal basePrice,
        Currency currency = Currency.KWD,
        CancellationPolicy? cancellationPolicy = null)
    {
        Guard.Against.Default(propertyId);
        Guard.Against.Null(name);
        Guard.Against.NegativeOrZero(maxOccupancy);
        Guard.Against.NegativeOrZero(basePrice);

        return new Unit(
            Guid.CreateVersion7(), propertyId, name, maxOccupancy, Money.Of(basePrice, currency),
            cancellationPolicy ?? CancellationPolicy.CreateDefault());
    }

    // A real mutation with its own invariant, not just a settable property -
    // callers don't re-derive the "must be positive" rule for themselves.
    // Takes a bare decimal, not Money, to keep UpdateUnitHandler's two-call
    // shape (SetBasePrice then SetCurrency) working - both funnel into the
    // one BasePrice field.
    public void SetBasePrice(decimal price)
    {
        Guard.Against.NegativeOrZero(price);
        BasePrice = Money.Of(price, BasePrice.Currency);
    }

    public void Rename(LocalizedText name)
    {
        Guard.Against.Null(name);
        Name = name;
    }

    public void SetMaxOccupancy(int maxOccupancy)
    {
        Guard.Against.NegativeOrZero(maxOccupancy);
        MaxOccupancy = maxOccupancy;
    }

    // Only ever changes what a unit is listed at going forward - existing
    // holds/bookings already snapshotted their own TotalPrice/Currency at
    // hold-creation time (see HoldAvailabilityHandler), so this can't
    // retroactively change what a guest already locked in.
    public void SetCurrency(Currency currency)
    {
        BasePrice = Money.Of(BasePrice.Amount, currency);
    }

    // Only governs bookings confirmed after this call - an existing
    // Booking already snapshotted its own CancellationPolicy at confirm
    // time (see ConfirmBookingHandler), same "can't retroactively worsen
    // terms a guest already agreed to" reasoning as SetCurrency's own note.
    public void SetCancellationPolicy(CancellationPolicy cancellationPolicy)
    {
        Guard.Against.Null(cancellationPolicy);
        CancellationPolicy = cancellationPolicy;
    }
}
