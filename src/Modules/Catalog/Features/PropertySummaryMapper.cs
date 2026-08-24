using Catalog.Entities;
using Catalog.Features.GetProperties;
namespace Catalog.Features;

// Shared by GetPropertiesHandler and GetMyPropertiesHandler as a plain
// method call, not through the Mediator dispatch layer - see docs/adr/0007
// for why those stay separate request/handler pairs despite both mapping
// this same shape.
internal static class PropertySummaryMapper
{
    public static List<PropertySummary> Map(IReadOnlyCollection<Property> properties)
    {
        // Materialize first, project after - see docs/adr/0006.
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
