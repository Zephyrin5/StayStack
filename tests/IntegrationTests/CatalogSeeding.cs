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
