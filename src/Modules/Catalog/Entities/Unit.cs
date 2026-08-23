using Ardalis.GuardClauses;
using Catalog.Enums;
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
        UnitType unitType,
        LocalizedText name,
        int maxOccupancy,
        decimal basePrice,
        Currency currency)
    {
        Id = id;
        PropertyId = propertyId;
        UnitType = unitType;
        Name = name;
        MaxOccupancy = maxOccupancy;
        BasePrice = basePrice;
        Currency = currency;
    }
    public Guid PropertyId { get; private set; }
    public UnitType UnitType { get; private set; }
    public LocalizedText Name { get; private set; }
    public int MaxOccupancy { get; private set; }
    public decimal BasePrice { get; private set; }
    public Currency Currency { get; private set; }

    public static Unit Create(
        Guid propertyId,
        UnitType unitType,
        LocalizedText name,
        int maxOccupancy,
        decimal basePrice,
        Currency currency = Currency.KWD)
    {
        Guard.Against.Default(propertyId);
        Guard.Against.Null(name);
        Guard.Against.NegativeOrZero(maxOccupancy);
        Guard.Against.NegativeOrZero(basePrice);

        return new Unit(Guid.CreateVersion7(), propertyId, unitType, name, maxOccupancy, basePrice, currency);
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
}
