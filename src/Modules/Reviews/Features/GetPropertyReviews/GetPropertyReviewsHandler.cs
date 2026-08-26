using BuildingBlocks.Pagination;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
namespace Reviews.Features.GetPropertyReviews;

public class GetPropertyReviewsHandler(AppReviewsDbContext dbContext)
    : IRequestHandler<GetPropertyReviewsRequest, GetPropertyReviewsResponse>
{
    public async ValueTask<GetPropertyReviewsResponse> Handle(GetPropertyReviewsRequest request, CancellationToken cancellationToken)
    {
        // Id as a tiebreaker, not deliberate sort criteria - see docs/adr/0008.
        (List<StayReview> reviews, int totalCount) = await dbContext.StayReviews
            .AsNoTracking()
            .Where(r => r.PropertyId == request.PropertyId)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        // A separate aggregate query over every review for this property,
        // not just the current page - the summary always reflects the
        // whole property, independent of pagination.
        var summary = await dbContext.StayReviews
            .AsNoTracking()
            .Where(r => r.PropertyId == request.PropertyId)
            .GroupBy(r => 1)
            .Select(g => new
            {
                Count = g.Count(),
                AverageOverall = g.Average(r => r.OverallRating),
                // Cast to decimal - int.Average() returns double, and the
                // rest of this DTO (including OverallRating above, already
                // decimal-typed on the entity) stays decimal throughout.
                AverageCleanliness = g.Average(r => (decimal)r.CleanlinessRating),
                AverageCommunication = g.Average(r => (decimal)r.CommunicationRating),
                AverageLocation = g.Average(r => (decimal)r.LocationRating),
                AverageValue = g.Average(r => (decimal)r.ValueRating),
                AverageAccuracy = g.Average(r => (decimal)r.AccuracyRating)
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new GetPropertyReviewsResponse
        {
            Reviews = new PagedResponse<StayReviewSummary>
            {
                Items =
                [
                    .. reviews.Select(r => new StayReviewSummary
                    {
                        Id = r.Id,
                        OverallRating = r.OverallRating,
                        CleanlinessRating = r.CleanlinessRating,
                        CommunicationRating = r.CommunicationRating,
                        LocationRating = r.LocationRating,
                        ValueRating = r.ValueRating,
                        AccuracyRating = r.AccuracyRating,
                        Comment = r.Comment,
                        HostReplyText = r.HostReplyText,
                        HostRepliedAt = r.HostRepliedAt,
                        CreatedAt = r.CreatedAt
                    })
                ],
                Page = request.Page,
                PageSize = request.PageSize,
                TotalCount = totalCount
            },
            RatingSummary = summary is null
                ? new RatingSummary()
                : new RatingSummary
                {
                    Count = summary.Count,
                    AverageOverall = summary.AverageOverall,
                    AverageCleanliness = summary.AverageCleanliness,
                    AverageCommunication = summary.AverageCommunication,
                    AverageLocation = summary.AverageLocation,
                    AverageValue = summary.AverageValue,
                    AverageAccuracy = summary.AverageAccuracy
                }
        };
    }
}
