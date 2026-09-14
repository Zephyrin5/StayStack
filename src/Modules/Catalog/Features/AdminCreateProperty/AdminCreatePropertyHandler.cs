using Persistence;
using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Localization;
using Catalog.Entities;
using Catalog.Features.CreateProperty;
using Hosts.Contracts;
using Mediator;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Catalog.Features.AdminCreateProperty;

public class AdminCreatePropertyHandler(
    AppCatalogDbContext dbContext,
    IHostLookup hostLookup,
    IOptions<LocalizationSettings> localizationSettings)
    : IRequestHandler<AdminCreatePropertyRequest, CreatePropertyResponse>
{
    public async ValueTask<CreatePropertyResponse> Handle(
        AdminCreatePropertyRequest request,
        CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid propertyId = Guid.CreateVersion7();

        // Unlike CreatePropertyHandler, HostId here IS trusted client
        // input - but "trusted" (an Administrator is allowed to specify
        // it) doesn't mean "assumed valid". Still confirm it's a real
        // Host before attaching a Property to it.
        if (!await hostLookup.ExistsAsync(request.HostId, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        Property property = Property.Create(propertyId, request.HostId, request.PropertyType, name, request.City, request.TimeZoneId);

        dbContext.Properties.Add(property);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsPrimaryKeyViolationOf<Property>(dbContext))
        {
            // A violation of this row's own primary key means an earlier attempt
            // committed and lost its acknowledgement - answer with that row
            // (Persistence.CommittedInsertRecovery). Nothing else is caught here:
            // no other unique index on this table has a domain answer.
            Property committed = (await dbContext.FindOwnCommittedInsertAsync<Property>(ex, property.Id, cancellationToken))!;
            return new CreatePropertyResponse { PropertyId = committed.Id };
        }

        return new CreatePropertyResponse { PropertyId = property.Id };
    }
}
