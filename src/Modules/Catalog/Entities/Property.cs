using Ardalis.GuardClauses;
using Catalog.Enums;
using SeedWork.Abstractions;
using SeedWork.ValueObjects;
namespace Catalog.Entities;

public sealed class Property : Entity
{

    // EF Core materializes through this constructor, not a parameterless
    // one plus reflection-set properties - it binds column values to
    // parameters by name, a built-in EF feature. This resolves the
    // conflict between `required` and private setters: no parameterless
    // constructor for `required` to reason about, and no `null!`
    // suppression needed, since every property gets a real,
    // compiler-verified value here.
    private Property(Guid id, Guid hostId, PropertyType propertyType, LocalizedText name, string? city)
    {
        Id = id;
        HostId = hostId;
        PropertyType = propertyType;
        Name = name;
        City = city;
    }
    public Guid HostId { get; private set; }
    public PropertyType PropertyType { get; private set; }
    public LocalizedText Name { get; private set; }
    public string? City { get; private set; }

    public static Property Create(
        Guid hostId,
        PropertyType propertyType,
        LocalizedText name,
        string? city)
    {
        Guard.Against.Default(hostId);
        Guard.Against.Null(name);

        return new Property(Guid.CreateVersion7(), hostId, propertyType, name, city);
    }

    public void Rename(LocalizedText name)
    {
        Guard.Against.Null(name);
        Name = name;
    }

    public void SetCity(string? city)
    {
        City = city;
    }

    public void SetPropertyType(PropertyType propertyType)
    {
        PropertyType = propertyType;
    }
}
