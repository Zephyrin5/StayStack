using Mediator;
namespace Reviews.Features.DeleteGuestReview;

public record DeleteGuestReviewRequest : IRequest<DeleteGuestReviewResponse>
{
    public Guid GuestReviewId { get; init; }
}
