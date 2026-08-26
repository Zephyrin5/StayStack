using Mediator;
namespace Reviews.Features.CreateStayReview;

// Public - a guest-checkout reviewer supplies ManagementToken (same token
// used to cancel/manage the booking, see Bookings' BookingManagementToken);
// an authenticated customer's own session is used instead and this stays
// null. See CreateStayReviewHandler for how the two are resolved through
// the same IBookingLookup.VerifyBookingAccessAsync call.
public record CreateStayReviewRequest : IRequest<CreateStayReviewResponse>
{
    public Guid BookingId { get; init; }
    public string? ManagementToken { get; init; }
    public int CleanlinessRating { get; init; }
    public int CommunicationRating { get; init; }
    public int LocationRating { get; init; }
    public int ValueRating { get; init; }
    public int AccuracyRating { get; init; }
    public string? Comment { get; init; }
}
