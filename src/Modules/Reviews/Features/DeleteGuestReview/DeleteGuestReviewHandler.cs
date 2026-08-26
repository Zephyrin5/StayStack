using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
namespace Reviews.Features.DeleteGuestReview;

public class DeleteGuestReviewHandler(
    AppReviewsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider) : IRequestHandler<DeleteGuestReviewRequest, DeleteGuestReviewResponse>
{
    public async ValueTask<DeleteGuestReviewResponse> Handle(DeleteGuestReviewRequest request, CancellationToken cancellationToken)
    {
        GuestReview review = await dbContext.GuestReviews
                                  .SingleOrDefaultAsync(r => r.Id == request.GuestReviewId, cancellationToken)
                              ?? throw new NotFoundException(nameof(GuestReview), request.GuestReviewId);

        review.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteGuestReviewResponse { GuestReviewId = review.Id };
    }
}
