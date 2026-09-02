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
    ///     <para>
    ///         This endpoint previously took a bare BookingId and nothing
    ///         else, unlike every other anonymous booking-scoped endpoint.
    ///         That made possession of the id the only credential, which is
    ///         also why the handler's "not found" versus "not payable"
    ///         answers were a status oracle: there was no caller identity to
    ///         withhold them from.
    ///     </para>
    /// </summary>
    public string? ManagementToken { get; init; }
}
