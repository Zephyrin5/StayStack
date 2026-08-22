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
}

public record ConfirmedHold
{
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
}
