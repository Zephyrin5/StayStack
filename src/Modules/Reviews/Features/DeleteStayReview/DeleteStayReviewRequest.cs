using Mediator;
namespace Reviews.Features.DeleteStayReview;

public record DeleteStayReviewRequest : IRequest<DeleteStayReviewResponse>
{
    public Guid StayReviewId { get; init; }
}
