using BuildingBlocks.Exceptions;
using System.Net;
namespace Bookings.Contracts;

/// <summary>
///     A payment succeeded for a booking whose hold is no longer claimable -
///     it was released or expired before the payment resolved, so the range
///     may already belong to another guest.
///     <para>
///         Money has been taken for inventory the platform no longer holds,
///         and nothing in code can put it right: re-taking the range would
///         double-book it, and silently confirming the booking would sell a
///         stay that cannot be honoured. The dispatcher's retry/dead-letter
///         path is the correct destination - it keeps the payment visible as
///         unresolved rather than closing it as success.
///     </para>
///     <para>
///         Reaching this means a payment outlived
///         <c>BookingLifecyclePolicyOptions.PaymentWindowMinutes</c>. That is
///         possible - a gateway can resolve slowly - which is why the window
///         is configurable and why this is an exception rather than an
///         assertion.
///     </para>
/// </summary>
public sealed class BookingHoldNoLongerHeldException(Guid bookingId, Guid holdId)
    : AppException(
        $"Payment for booking '{bookingId}' succeeded, but hold '{holdId}' is no longer held - " +
        "it was released or expired before the payment resolved.",
        (int)HttpStatusCode.Conflict);
