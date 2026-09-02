using BuildingBlocks.Localization;
using Hosts.Entities;
using Mediator;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Hosts.Features.CreateHost;

public class CreateHostHandler(AppHostsDbContext dbContext, IOptions<LocalizationSettings> localizationSettings)
    : IRequestHandler<CreateHostRequest, CreateHostResponse>
{
    public async ValueTask<CreateHostResponse> Handle(CreateHostRequest request, CancellationToken cancellationToken)
    {
        LocalizedText? displayName = request.DisplayName is { Count: > 0 }
            ? LocalizedText.Create(request.DisplayName, localizationSettings.Value.DefaultCulture)
            : null;

        // Generated here: this is the admin-facing create, with no cross-module
        // retry story to make idempotent - unlike BecomeHost, whose id comes
        // from a PendingHostLinkIntent recorded before the call.
        Host host = Host.Create(
            Guid.CreateVersion7(), request.BusinessName, request.ContactEmail, request.ContactPhone, displayName);

        dbContext.Hosts.Add(host);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateHostResponse { HostId = host.Id };
    }
}
