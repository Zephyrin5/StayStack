namespace Hosts.Contracts;

/// <summary>
///     Deliberately minimal - just enough for Catalog to validate a HostId
///     before attaching a Property to it, without Catalog ever referencing
///     Hosts' own entities or AppHostsDbContext directly. Same pattern
///     planned for a future IUnitLookup (Booking depending on Catalog).
///     Lives in its own project so that guarantee is compiler-enforced:
///     Catalog references Hosts.Contracts, never Hosts itself, so it has
///     no way to reach Hosts.Entities or AppHostsDbContext even by accident.
/// </summary>
public interface IHostLookup
{
    Task<bool> ExistsAsync(Guid hostId, CancellationToken cancellationToken);
}