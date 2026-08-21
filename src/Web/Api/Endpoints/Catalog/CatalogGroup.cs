using FastEndpoints;
namespace Api.Endpoints.Catalog;

public sealed class CatalogGroup : Group
{
    public CatalogGroup()
    {
        Configure("api/catalog", ep => { ep.Description(b => b.WithTags("Catalog")); });
    }
}
