namespace Hosts.Contracts;

/// <summary>
///     Write-side counterpart to IHostLookup. Identity's BecomeHost feature
///     depends on this instead of AppHostsDbContext, same boundary
///     reasoning as IHostLookup - Identity never sees a Host entity.
/// </summary>
public interface IHostRegistrar
{
    /// <summary>
    ///     Registers a Host under a caller-supplied id, idempotently: calling
    ///     it again with the same id is a no-op rather than a second Host.
    ///     <para>
    ///         The id comes from the caller because the caller durably records it
    ///         (Identity's PendingHostLinkIntent) before calling, so a retry after
    ///         a timeout re-registers the same Host instead of orphaning one per
    ///         attempt.
    ///     </para>
    /// </summary>
    Task RegisterHostAsync(
        Guid hostId,
        string businessName,
        string contactEmail,
        string? contactPhone,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Compensating action only - for undoing a RegisterHostAsync call
    ///     when the follow-up write on the Identity side (linking HostId,
    ///     adding the Host role) fails. This is a genuine hard delete, not
    ///     Entity.Archive - a Host that never successfully finished
    ///     registration was never really visible to anyone else in the
    ///     system, so there's nothing worth preserving. A Host actually
    ///     leaving the platform later is a different, real business event
    ///     and should go through Archive instead, not this.
    /// </summary>
    Task DeleteAsync(Guid hostId, CancellationToken cancellationToken);
}
