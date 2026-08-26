using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.GetBookingForManagement;

public record GetBookingForManagementResponse
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public BookingStatus BookingStatus { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }

    public bool CanCancel { get; init; }

    // Confirmed + checkout passed - the eligibility gate Reviews itself
    // re-checks via IBookingLookup.VerifyBookingAccessAsync before actually
    // accepting a review. Doesn't account for whether a review already
    // exists - Bookings has no dependency on Reviews to know that; the
    // client asks Reviews separately when it loads the review form, same
    // as the property rating summary is its own independent call.
    public bool CanReview { get; init; }
}
