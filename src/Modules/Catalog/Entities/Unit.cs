using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.Interfaces;
using SeedWork.ValueObjects;
namespace Catalog.Entities;

public sealed class Unit : Entity, IAggregateRoot
{

    // See Property.cs for why materialization goes through a real
    // constructor rather than a parameterless one + `required`/`null!`.
    private Unit(
        Guid id,
        Guid propertyId,
        LocalizedText name,
        int maxOccupancy,
        decimal basePrice,
        Currency currency)
    {
        Id = id;
        PropertyId = propertyId;
        Name = name;
        MaxOccupancy = maxOccupancy;
        BasePrice = basePrice;
        Currency = currency;
    }
    public Guid PropertyId { get; private set; }
    public LocalizedText Name { get; private set; }
    public int MaxOccupancy { get; private set; }
    public decimal BasePrice { get; private set; }
    public Currency Currency { get; private set; }

    public static Unit Create(
        Guid propertyId,
        LocalizedText name,
        int maxOccupancy,
        decimal basePrice,
        Currency currency = Currency.KWD)
    {
        Guard.Against.Default(propertyId);
        Guard.Against.Null(name);
        Guard.Against.NegativeOrZero(maxOccupancy);
        Guard.Against.NegativeOrZero(basePrice);

        return new Unit(Guid.CreateVersion7(), propertyId, name, maxOccupancy, basePrice, currency);
    }

    // A real mutation with its own invariant, not just a settable property -
    // this is where price changes get funneled through once the admin
    // Catalog endpoints exist, rather than each caller re-deriving the
    // "must be positive" rule for itself.
    public void SetBasePrice(decimal price)
    {
        Guard.Against.NegativeOrZero(price);
        BasePrice = price;
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
        Currency = currency;
    }
}
