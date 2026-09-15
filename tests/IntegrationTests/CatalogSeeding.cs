using BuildingBlocks.Time;
using Catalog.Entities;
using Catalog.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests;

/// <summary>
///     Builds the Property a test Unit needs to be valid.
///     <para>
///         A bare Unit against a throwaway PropertyId is a shape the database
///         should never hold, and it does not resolve: every business date is
///         computed in the owning property's timezone (docs/adr/0018), so
///         UnitLookup raises OrphanedUnitException for a unit with no property
///         rather than defaulting to UTC.
///     </para>
/// </summary>
internal static class CatalogSeeding
{
    // Matches what the migration backfills existing rows to, and the market
    // the rest of these fixtures assume (KWD, Kuwait City). Deliberately a
    // UTC+3 zone: a test pinned to a late-evening UTC instant lands on the
    // next local day here, which is what makes the boundary tests bite.
    public const string TestTimeZoneId = "Asia/Kuwait";

    /// <summary>
    ///     "Today" as the seeded property reckons it. Any test that seeds a
    ///     stay date against a <see cref="CreateProperty"/> property must use
    ///     this rather than a UTC date, because that is the clock
    ///     HoldAvailabilityHandler validates CheckIn on (docs/adr/0018).
    ///     <para>
    ///         Getting this wrong is invisible for 21 hours a day: between 21:00
    ///         and 24:00 UTC, Kuwait is already on the next date, so a UTC
    ///         "today" is yesterday locally and every hold is rejected with
    ///         "Check-in date cannot be in the past."
    ///     </para>
    /// </summary>
    public static DateOnly Today(TimeProvider? timeProvider = null) =>
        PropertyTimeZone.Today(timeProvider ?? TimeProvider.System, TestTimeZoneId);

    public static Property CreateProperty(string timeZoneId = TestTimeZoneId) =>
        Property.Create(
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            PropertyType.Hotel,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Test Property" } }, "en"),
            "Kuwait City",
            timeZoneId);

    public static Unit CreateUnit(
        Property property,
        decimal basePrice = 100m,
        int maxOccupancy = 2,
        string name = "Standard Room") =>
        Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", name } }, "en"),
            maxOccupancy,
            basePrice);
}
