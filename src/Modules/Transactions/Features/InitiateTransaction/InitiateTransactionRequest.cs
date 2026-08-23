using Mediator;
namespace Transactions.Features.InitiateTransaction;

public record InitiateTransactionRequest : IRequest<InitiateTransactionResponse>
{
    public Guid BookingId { get; init; }
}
