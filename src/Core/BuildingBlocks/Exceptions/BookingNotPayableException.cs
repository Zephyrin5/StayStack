using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     The booking isn't in a state a transaction can be initiated for -
///     already Confirmed (already paid) or Cancelled. Thrown by
///     InitiateTransactionHandler (Transactions).
/// </summary>
public sealed class BookingNotPayableException(Guid bookingId)
    : AppException($"Booking '{bookingId}' is not payable.", (int)HttpStatusCode.Conflict);
