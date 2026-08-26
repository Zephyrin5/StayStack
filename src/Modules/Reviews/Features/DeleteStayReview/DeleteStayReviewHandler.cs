using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
namespace Reviews.Features.DeleteStayReview;

public class DeleteStayReviewHandler(
    AppReviewsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider) : IRequestHandler<DeleteStayReviewRequest, DeleteStayReviewResponse>
{
    public async ValueTask<DeleteStayReviewResponse> Handle(DeleteStayReviewRequest request, CancellationToken cancellationToken)
    {
        StayReview review = await dbContext.StayReviews
                                 .SingleOrDefaultAsync(r => r.Id == request.StayReviewId, cancellationToken)
                             ?? throw new NotFoundException(nameof(StayReview), request.StayReviewId);

        review.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteStayReviewResponse { StayReviewId = review.Id };
    }
}
