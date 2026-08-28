using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Reviews.Entities;
namespace Reviews.Features.ReplyToStayReview;

public class ReplyToStayReviewHandler(
    AppReviewsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    TimeProvider timeProvider) : IRequestHandler<ReplyToStayReviewRequest, ReplyToStayReviewResponse>
{
    public async ValueTask<ReplyToStayReviewResponse> Handle(ReplyToStayReviewRequest request, CancellationToken cancellationToken)
    {
        StayReview review = await dbContext.StayReviews
                                 .SingleOrDefaultAsync(r => r.Id == request.StayReviewId, cancellationToken)
                             ?? throw new NotFoundException(nameof(StayReview), request.StayReviewId);

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(review.HostId, nameof(StayReview), review.Id);
        }

        review.Reply(request.ReplyText, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ReplyToStayReviewResponse { StayReviewId = review.Id };
    }
}
