using Catalog.Entities;
using Catalog.Features.GetProperties;
namespace Catalog.Features;

// Shared by GetPropertiesHandler and GetMyPropertiesHandler - the query
// differs (public/filtered vs. caller's own host), but both end up mapping
// the same Property -> PropertySummary shape. Reusing this method, not the
// Mediator request/handler, is the point: it avoids duplicating the
// materialize-first-map-after workaround below without collapsing the two
// endpoints' telemetry/trust boundaries into one Mediator request type -
// see the two handlers' own comments for why that mattered.
internal static class PropertySummaryMapper
{
    public static List<PropertySummary> Map(IReadOnlyCollection<Property> properties)
    {
        // Materialize first, project after - Name is a LocalizedText (a
        // value-converted jsonb column via StayStackDbContext's global
        // convention), and EF Core can't translate .Values access on a
        // converted CLR type into SQL inside a server-side .Select().
        return
        [
            .. properties.Select(p => new PropertySummary
            {
                Id = p.Id,
                HostId = p.HostId,
                PropertyType = p.PropertyType,
                Name = new Dictionary<string, string>(p.Name.Values),
                City = p.City
            })
        ];
    }
}
