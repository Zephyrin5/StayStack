namespace Hosts.Contracts;

/// <summary>
///     The mechanism behind "never trust HostId from the client". Two operations rather than one
///     comparison because the cases differ: creating a property has no existing resource to check
///     against, while creating a unit targets a property whose HostId must be resolved first. One
///     enforcement point, so a later tenant-scoped handler does not reimplement the comparison.
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
