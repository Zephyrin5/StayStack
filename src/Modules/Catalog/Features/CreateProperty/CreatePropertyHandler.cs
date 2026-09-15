using Persistence;
using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Localization;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Catalog.Features.CreateProperty;

public class CreatePropertyHandler(
    AppCatalogDbContext dbContext,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<CreatePropertyRequest, CreatePropertyResponse>
{
    public async ValueTask<CreatePropertyResponse> Handle(
        CreatePropertyRequest request,
        CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid propertyId = Guid.CreateVersion7();

        // Throws NotAHostException if the caller has no host_id claim. No
        // IHostLookup needed: only BecomeHost sets a HostId that reaches the
        // token, so it references a real Host by construction.
        Guid hostId = hostAuthorization.RequireHostId();

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        Property property = Property.Create(propertyId, hostId, request.PropertyType, name, request.City, request.TimeZoneId);

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
            Property committed = await dbContext.FindOwnCommittedInsertAsync<Property>(property.Id, cancellationToken);
            return new CreatePropertyResponse { PropertyId = committed.Id };
        }

        return new CreatePropertyResponse { PropertyId = property.Id };
    }
}
