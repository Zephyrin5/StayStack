using BuildingBlocks.Time;
using Catalog.Entities;
using Catalog.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests;

/// <summary>
///     Builds the Property a test Unit needs to be valid.
///     <para>
///         Fixtures used to construct a bare Unit against a throwaway
///         PropertyId with no matching Property row. That shape is not one the
///         database should ever hold, and since docs/adr/0018 it no longer
///         resolves: every business date is computed in the owning property's
///         timezone, so a unit without a property has no zone and
///         UnitLookup raises OrphanedUnitException rather than defaulting to
///         UTC and silently shifting booking and refund boundaries.
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
    ///     this rather than CatalogSeeding.Today(), because
    ///     that is the clock HoldAvailabilityHandler validates CheckIn on
    ///     (docs/adr/0018).
    ///     <para>
    ///         Getting this wrong is invisible for 21 hours a day and then
    ///         fails everything: between 21:00 and 24:00 UTC, Kuwait is
    ///         already on the next date, so a UTC-derived "today" is
    ///         yesterday locally and every hold is rejected with "Check-in
    ///         date cannot be in the past." The whole reason this fixture
    ///         picked a UTC+3 zone was to make that skew reachable - it just
    ///         reached further than intended.
    ///     </para>
    /// </summary>
    public static DateOnly Today(TimeProvider? timeProvider = null) =>
        PropertyTimeZone.Today(timeProvider ?? TimeProvider.System, TestTimeZoneId);

    public static Property CreateProperty(string timeZoneId = TestTimeZoneId) =>
        Property.Create(
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
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", name } }, "en"),
            maxOccupancy,
            basePrice);
}
