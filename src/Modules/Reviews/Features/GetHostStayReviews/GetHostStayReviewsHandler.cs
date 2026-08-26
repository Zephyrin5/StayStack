using BuildingBlocks.Pagination;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
using Reviews.Features.GetPropertyReviews;
namespace Reviews.Features.GetHostStayReviews;

public class GetHostStayReviewsHandler(
    AppReviewsDbContext dbContext,
    IHostAuthorization hostAuthorization) : IRequestHandler<GetHostStayReviewsRequest, PagedResponse<StayReviewSummary>>
{
    public async ValueTask<PagedResponse<StayReviewSummary>> Handle(
        GetHostStayReviewsRequest request, CancellationToken cancellationToken)
    {
        Guid hostId = hostAuthorization.RequireHostId();

        // Id as a tiebreaker, not deliberate sort criteria - see docs/adr/0008.
        (List<StayReview> reviews, int totalCount) = await dbContext.StayReviews
            .AsNoTracking()
            .Where(r => r.HostId == hostId)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResponse<StayReviewSummary>
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
        };
    }
}
