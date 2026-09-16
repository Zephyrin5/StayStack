using Persistence;
using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Localization;
using Hosts.Entities;
using Mediator;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Hosts.Features.CreateHost;

public class CreateHostHandler(HostsDb dbContext, IOptions<LocalizationSettings> localizationSettings)
    : IRequestHandler<CreateHostRequest, CreateHostResponse>
{
    public async ValueTask<CreateHostResponse> Handle(CreateHostRequest request, CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid hostId = Guid.CreateVersion7();

        LocalizedText? displayName = request.DisplayName is { Count: > 0 }
            ? LocalizedText.Create(request.DisplayName, localizationSettings.Value.DefaultCulture)
            : null;

        // The admin-facing create. The save below runs under the execution
        // strategy, so a lost acknowledgement is recovered by the id minted above.
        Host host = Host.Create(
            hostId, request.BusinessName, request.ContactEmail, request.ContactPhone, displayName);

        dbContext.Hosts.Add(host);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsPrimaryKeyViolationOf(dbContext.Hosts))
        {
            // A violation of this row's own primary key means an earlier attempt
            // committed and lost its acknowledgement - answer with that row
            // (Persistence.CommittedInsertRecovery). Nothing else is caught here:
            // no other unique index on this table has a domain answer.
            Host committed = await dbContext.Hosts.FindOwnCommittedInsertAsync(host.Id, cancellationToken);
            return new CreateHostResponse { HostId = committed.Id };
        }

        return new CreateHostResponse { HostId = host.Id };
    }
}
