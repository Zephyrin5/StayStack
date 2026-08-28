using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Catalog.Features.UpdateProperty;

public class UpdatePropertyHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<UpdatePropertyRequest, UpdatePropertyResponse>
{
    public async ValueTask<UpdatePropertyResponse> Handle(UpdatePropertyRequest request, CancellationToken cancellationToken)
    {
        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), request.PropertyId);
        }

        // Same Administrator-bypass/Host-ownership split as CreateUnitHandler.
        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), request.PropertyId);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        property.Rename(name);
        property.SetPropertyType(request.PropertyType);
        property.SetCity(request.City);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new UpdatePropertyResponse { PropertyId = property.Id };
    }
}
