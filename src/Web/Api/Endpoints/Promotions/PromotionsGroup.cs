using FastEndpoints;
namespace Api.Endpoints.Promotions;

public sealed class PromotionsGroup : Group
{
    public PromotionsGroup()
    {
        Configure("api/promotions", ep => { ep.Description(b => b.WithTags("Promotions")); });
    }
}
