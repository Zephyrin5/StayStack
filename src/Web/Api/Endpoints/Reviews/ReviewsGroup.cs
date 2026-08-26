using FastEndpoints;
namespace Api.Endpoints.Reviews;

public sealed class ReviewsGroup : Group
{
    public ReviewsGroup()
    {
        Configure("api/reviews", ep => { ep.Description(b => b.WithTags("Reviews")); });
    }
}
