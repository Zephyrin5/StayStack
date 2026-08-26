using BuildingBlocks.Exceptions;
using System.Net;
namespace Reviews.Exceptions;

/// <summary>
///     A booking's guest only ever gets one GuestReview - the unique index
///     on GuestReview.BookingId is the real guarantee (see
///     CreateGuestReviewHandler's check-first-then-catch idiom); this is the
///     friendly error surfaced either way.
/// </summary>
public sealed class GuestAlreadyReviewedException(Guid bookingId)
    : AppException($"Booking '{bookingId}' already has a guest review.", (int)HttpStatusCode.Conflict);
