using BuildingBlocks.Exceptions;
using System.Net;
namespace Bookings.Contracts;

/// <summary>
///     The stay has already begun, so there is nothing left to cancel.
///     <para>
///         A 409 rather than a 404, and the distinction is the point: the
///         caller was correctly authorized to reach this booking - an
///         authenticated customer's own, or a guest management link still
///         inside its lifetime - and is being refused on eligibility, not on
///         identity. Answering 404 would tell a guest their booking had
///         vanished when they can plainly see it.
///     </para>
/// </summary>
public sealed class BookingNotCancellableException(Guid bookingId)
    : AppException(
        $"Booking '{bookingId}' can no longer be cancelled - its stay has already started.",
        (int)HttpStatusCode.Conflict);
