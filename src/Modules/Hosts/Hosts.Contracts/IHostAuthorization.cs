namespace Hosts.Contracts;

/// <summary>
///     The actual mechanism behind "never trust HostId from the client".
///     Two distinct operations, not one generic comparison, because the
///     two real cases genuinely differ: CreateProperty has no existing
///     resource to check against (the caller either is a host or isn't),
///     while CreateUnit targets an existing Property whose HostId has to
///     be resolved (a DB lookup) before there's anything to compare.
///     Both funnel through here so future tenant-scoped handlers
///     (UpdateProperty, host transaction views, etc.) reuse one
///     enforcement point instead of each reimplementing the comparison.
/// </summary>
public interface IHostAuthorization
{
    /// <summary>
    ///     Returns the caller's own HostId, or throws NotAHostException if
    ///     they have none. Use where the caller must simply be a host at
    ///     all - no specific resource to check ownership against yet.
    /// </summary>
    Guid RequireHostId();

    /// <summary>
    ///     Throws if the caller's HostId doesn't match resourceHostId.
    ///     Deliberately throws NotFoundException, not a 403 - revealing
    ///     "this resource exists but belongs to someone else" would let a
    ///     caller enumerate other hosts' resources by testing IDs and
    ///     watching the status code change. From the outside, "doesn't
    ///     exist" and "exists but isn't yours" must look identical.
    /// </summary>
    void RequireOwnership(Guid resourceHostId, string resourceName, object resourceKey);
}