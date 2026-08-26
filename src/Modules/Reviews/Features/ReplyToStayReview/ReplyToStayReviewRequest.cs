using Mediator;
namespace Reviews.Features.ReplyToStayReview;

public record ReplyToStayReviewRequest : IRequest<ReplyToStayReviewResponse>
{
    public Guid StayReviewId { get; init; }
    public required string ReplyText { get; init; }
}
