namespace Reviews.Features.DeleteGuestReview;

public record DeleteGuestReviewResponse
{
    public Guid GuestReviewId { get; init; }
}
