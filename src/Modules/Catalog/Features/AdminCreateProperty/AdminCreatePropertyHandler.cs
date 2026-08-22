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
        // Unlike CreatePropertyHandler, HostId here IS trusted client
        // input - but "trusted" (an Administrator is allowed to specify
        // it) doesn't mean "assumed valid". Still confirm it's a real
        // Host before attaching a Property to it.
        if (!await hostLookup.ExistsAsync(request.HostId, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        Property property = Property.Create(request.HostId, request.PropertyType, name, request.City);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatePropertyResponse { PropertyId = property.Id };
    }
}
