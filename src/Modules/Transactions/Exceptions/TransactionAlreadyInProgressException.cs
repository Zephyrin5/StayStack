using BuildingBlocks.Exceptions;
using System.Net;
namespace Transactions.Exceptions;

/// <summary>
///     A Pending or Succeeded transaction already exists for this booking -
///     thrown by InitiateTransactionHandler to prevent double-charging the
///     same booking.
/// </summary>
public sealed class TransactionAlreadyInProgressException(Guid bookingId)
    : AppException($"A transaction is already in progress for booking '{bookingId}'.", (int)HttpStatusCode.Conflict);
