using SeedWork.Enums;
namespace Catalog.Contracts;

/// <summary>
///     Write-side counterpart to IUnitLookup - lets Bookings turn a Hold
///     into a real booking without ever seeing UnitAvailabilityHold or
///     touching unit_availability_holds directly. Same boundary reasoning
///     as Hosts.Contracts.IHostRegistrar.
/// </summary>
public interface IHoldConfirmation
{
    /// <summary>
    ///     Marks the hold as booked (status 'held' -> 'booked'). Throws
    ///     NotFoundException if the hold doesn't exist, has already been
    ///     consumed, or has expired - Bookings never sees a stale/expired
    ///     hold succeed silently.
    /// </summary>
    Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    ///     Reverts 'booked' back to 'held' with hold_expires_at reset to
    ///     now - used both as ConfirmBookingHandler's compensating action
    ///     when its Booking write fails, and by CancelBookingHandler to
    ///     free the range back up immediately rather than leaving it
    ///     blocked for whatever was left on the hold's original 15-minute
    ///     window. Best-effort/idempotent: a no-op if the hold is no longer
    ///     'booked' (already released, or never existed).
    /// </summary>
    Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken);
}

public record ConfirmedHold
{
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }
}
