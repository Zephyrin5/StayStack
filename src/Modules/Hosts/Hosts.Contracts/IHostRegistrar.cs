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
    ///         It used to generate the id and return it, which made a retry
    ///         after a timeout indistinguishable from a first attempt - the
    ///         caller's "am I already a host" guard still saw null, so a
    ///         client retrying three times on a flaky connection left three
    ///         orphaned Hosts. The id has to come from the caller because the
    ///         caller is what durably records it (Identity's
    ///         PendingHostLinkIntent) before this is ever called.
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
