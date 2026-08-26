using FastEndpoints;
namespace Api.Endpoints.Catalog;

// No shared "Catalog" tag set here, deliberately - each endpoint sets its
// own more specific tag instead (Properties/Units/Pricing Rules/
// Promotions/Availability), and Program.cs's x-tagGroups document
// transformer nests those under a virtual "Catalog" heading in Scalar's
// sidebar. Setting "Catalog" here too would have every endpoint carry both
// tags (WithTags is additive, not a replace), which would show a
// duplicate, un-nested "Catalog" section alongside the real nested one.
public sealed class CatalogGroup : Group
{
    public CatalogGroup()
    {
        Configure("api/catalog", _ => { });
    }
}
