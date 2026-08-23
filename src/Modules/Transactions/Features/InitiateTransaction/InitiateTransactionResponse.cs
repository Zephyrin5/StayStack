using SeedWork.Enums;
using Transactions.Entities;
namespace Transactions.Features.InitiateTransaction;

public record InitiateTransactionResponse
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public decimal Amount { get; init; }
    public Currency Currency { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
}
