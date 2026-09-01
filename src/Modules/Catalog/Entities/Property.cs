using Ardalis.GuardClauses;
using BuildingBlocks.Time;
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
    private Property(Guid id, Guid hostId, PropertyType propertyType, LocalizedText name, string? city, string timeZoneId)
    {
        Id = id;
        HostId = hostId;
        PropertyType = propertyType;
        Name = name;
        City = city;
        TimeZoneId = timeZoneId;
    }
    public Guid HostId { get; private set; }
    public PropertyType PropertyType { get; private set; }
    public LocalizedText Name { get; private set; }
    public string? City { get; private set; }

    // The IANA zone every business date for this property is resolved in
    // (docs/adr/0018). Required, unlike City: a date computed in the wrong
    // zone silently shifts same-day bookability and refund tiers, so there is
    // no defensible "unknown" value to fall back to at read time. City stays
    // nullable free text and is deliberately not used to infer this.
    public string TimeZoneId { get; private set; }

    public static Property Create(
        Guid hostId,
        PropertyType propertyType,
        LocalizedText name,
        string? city,
        string timeZoneId)
    {
        Guard.Against.Default(hostId);
        Guard.Against.Null(name);
        GuardTimeZone(timeZoneId);

        return new Property(Guid.CreateVersion7(), hostId, propertyType, name, city, timeZoneId);
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

    /// <summary>
    ///     Changing this moves every *future* business date for the property.
    ///     It does not move existing bookings: those snapshot their own zone
    ///     at confirm time (see Booking.TimeZoneId), so a host correcting a
    ///     mis-entered zone can't retroactively shift a guest's refund
    ///     boundary.
    /// </summary>
    public void SetTimeZoneId(string timeZoneId)
    {
        GuardTimeZone(timeZoneId);
        TimeZoneId = timeZoneId;
    }

    // InvalidInput, not a raw TimeZoneInfo.FindSystemTimeZoneById call - that
    // throws TimeZoneNotFoundException, which sits outside the
    // ArgumentException family GlobalExceptionHandler maps to 400, so this
    // backstop would surface a validation failure as a 500. The write
    // validators reject bad ids first; this is the guard behind them.
    private static void GuardTimeZone(string timeZoneId)
    {
        Guard.Against.NullOrWhiteSpace(timeZoneId);
        Guard.Against.InvalidInput(timeZoneId, nameof(timeZoneId),
            PropertyTimeZone.IsValid,
            $"'{timeZoneId}' is not a recognised time zone identifier.");
    }
}
