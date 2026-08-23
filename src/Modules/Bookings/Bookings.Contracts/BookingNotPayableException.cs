using BuildingBlocks.Exceptions;
using System.Net;
namespace Bookings.Contracts;

/// <summary>
///     The booking isn't in a state a transaction can be initiated for -
///     already Confirmed (already paid) or Cancelled. Lives here, not
///     Bookings' own Exceptions/ folder: this is the one exception in the
///     solution actually thrown from two places - Booking.Confirm() (the
///     authoritative check, on Bookings' own aggregate) and
///     InitiateTransactionHandler (Transactions), which re-derives the same
///     conclusion from BookingSummary.IsPending - a fact Bookings already
///     handed it through this same Contracts project. Not a coincidence of
///     two unrelated modules reaching for the same word; Transactions is
///     deferring to Bookings' own invariant, which is exactly what
///     Contracts projects are for exposing.
/// </summary>
public sealed class BookingNotPayableException(Guid bookingId)
    : AppException($"Booking '{bookingId}' is not payable.", (int)HttpStatusCode.Conflict);
