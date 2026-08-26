using Mediator;
namespace Reviews.Features.CreateGuestReview;

public record CreateGuestReviewRequest : IRequest<CreateGuestReviewResponse>
{
    public Guid BookingId { get; init; }
    public int OverallRating { get; init; }
    public string? Comment { get; init; }
}
