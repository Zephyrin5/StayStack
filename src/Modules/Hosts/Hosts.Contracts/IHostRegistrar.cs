namespace Hosts.Contracts;

/// <summary>
///     Write-side counterpart to IHostLookup. Identity's BecomeHost feature
///     depends on this instead of HostsDb, same boundary
///     reasoning as IHostLookup - Identity never sees a Host entity.
/// </summary>
public interface IHostRegistrar
{
    /// <summary>
    ///     Registers a Host under a caller-supplied id, idempotently: calling
    ///     it again with the same id is a no-op rather than a second Host.
    ///     <para>
    ///         Runs inside the caller's transaction (BecomeHostHandler), so the
    ///         Host commits only with the caller's link to it.
    ///     </para>
    /// </summary>
    Task RegisterHostAsync(
        Guid hostId,
        string businessName,
        string contactEmail,
        string? contactPhone,
        CancellationToken cancellationToken);
}
