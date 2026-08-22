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
        // Throws NotAHostException if the caller has no host_id claim -
        // this also means IHostLookup isn't needed here at all anymore:
        // a HostId that made it into the token was only ever set by
        // BecomeHost, so it's guaranteed to reference a real Host by
        // construction, not something that needs re-validating per call.
        Guid hostId = hostAuthorization.RequireHostId();

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        Property property = Property.Create(hostId, request.PropertyType, name, request.City);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatePropertyResponse { PropertyId = property.Id };
    }
}
