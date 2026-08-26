using BuildingBlocks.Exceptions;
using System.Net;
namespace Reviews.Exceptions;

/// <summary>
///     A booking only ever gets one StayReview - the unique index on
///     StayReview.BookingId is the real guarantee (see
///     CreateStayReviewHandler's check-first-then-catch idiom); this is the
///     friendly error surfaced either way.
/// </summary>
public sealed class StayAlreadyReviewedException(Guid bookingId)
    : AppException($"Booking '{bookingId}' has already been reviewed.", (int)HttpStatusCode.Conflict);
