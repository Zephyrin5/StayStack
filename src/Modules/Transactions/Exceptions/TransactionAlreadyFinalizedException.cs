using BuildingBlocks.Exceptions;
using System.Net;
namespace Transactions.Exceptions;

/// <summary>
///     The transaction has already succeeded or failed - only a Pending
///     transaction can transition. Thrown by Transaction.MarkSucceeded()/
///     MarkFailed().
/// </summary>
public sealed class TransactionAlreadyFinalizedException(Guid transactionId)
    : AppException($"Transaction '{transactionId}' has already been finalized.", (int)HttpStatusCode.Conflict);
