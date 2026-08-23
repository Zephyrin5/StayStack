using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     A Pending or Succeeded transaction already exists for this booking -
///     thrown by InitiateTransactionHandler (Transactions) to prevent
///     double-charging the same booking.
/// </summary>
public sealed class TransactionAlreadyInProgressException(Guid bookingId)
    : AppException($"A transaction is already in progress for booking '{bookingId}'.", (int)HttpStatusCode.Conflict);
