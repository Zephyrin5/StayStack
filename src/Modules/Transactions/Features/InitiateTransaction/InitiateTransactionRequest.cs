using BuildingBlocks.Observability;
using Mediator;
namespace Transactions.Features.InitiateTransaction;

public record InitiateTransactionRequest : IRequest<InitiateTransactionResponse>
{
    public Guid BookingId { get; init; }

    /// <summary>
    ///     Proof of ownership for a guest-checkout caller, exactly as
    ///     CancelBookingRequest and GetBookingForManagementRequest carry it.
    ///     Optional because an authenticated customer proves ownership with
    ///     their CustomerId instead - BookingAccessChecker accepts either.
    /// </summary>

}
