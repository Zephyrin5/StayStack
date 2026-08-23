using SeedWork.Enums;
using Transactions.Entities;
namespace Transactions.Features.GetTransactions;

public record TransactionSummary
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public decimal Amount { get; init; }
    public Currency Currency { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
