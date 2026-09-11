using BuildingBlocks.Observability;
namespace Bookings.Features.CreateBookingSession;

public record CreateBookingSessionResponse
{
    [Sensitive] public required string SessionToken { get; init; }

    /// <summary>
    ///     So a client can schedule its own re-exchange rather than discover
    ///     expiry as a 404 mid-action. The link the guest already has stays
    ///     valid, so re-exchanging costs them nothing.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
