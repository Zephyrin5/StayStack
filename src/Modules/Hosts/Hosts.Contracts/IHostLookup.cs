namespace Hosts.Contracts;

/// <summary>
///     Just enough for Catalog to validate a HostId before attaching a Property to it, without
///     referencing Hosts' entities or HostsDb. Its own project makes that compiler-enforced: Catalog
///     references Hosts.Contracts, never Hosts (docs/adr/0004).
/// </summary>
public interface IHostLookup
{
    Task<bool> ExistsAsync(Guid hostId, CancellationToken cancellationToken);
}
