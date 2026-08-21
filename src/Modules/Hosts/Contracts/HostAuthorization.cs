using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
namespace Hosts.Contracts;

// internal, same reasoning as HostRegistrar - Catalog/Identity should only
// ever reach this through IHostAuthorization, resolved via DI.
internal class HostAuthorization(ICurrentUserProvider currentUserProvider) : IHostAuthorization
{
    public Guid RequireHostId() =>
        currentUserProvider.HostId ?? throw new NotAHostException();

    public void RequireOwnership(Guid resourceHostId, string resourceName, object resourceKey)
    {
        Guid callerHostId = RequireHostId();

        if (callerHostId != resourceHostId)
        {
            throw new NotFoundException(resourceName, resourceKey);
        }
    }
}
