namespace Hosts.Contracts;

/// <summary>
///     Write-side counterpart to IHostLookup. Identity's BecomeHost feature
///     depends on this instead of AppHostsDbContext, same boundary
///     reasoning as IHostLookup - Identity never sees a Host entity.
/// </summary>
public interface IHostRegistrar
{
    Task<Guid> RegisterHostAsync(
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