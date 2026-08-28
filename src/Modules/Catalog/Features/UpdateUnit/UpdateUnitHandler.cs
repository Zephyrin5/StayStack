using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.UpdateUnit;

public class UpdateUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<UpdateUnitRequest, UpdateUnitResponse>
{
    public async ValueTask<UpdateUnitResponse> Handle(UpdateUnitRequest request, CancellationToken cancellationToken)
    {
        Unit? unit = await dbContext.Units
            .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken);

        if (unit is null)
        {
            throw new NotFoundException(nameof(Unit), request.UnitId);
        }

        // Same reasoning as CreateUnitHandler: ownership is really the
        // owning Property's, so it has to be loaded and checked, not
        // read off the Unit itself.
        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == unit.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), unit.PropertyId);
        }

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        unit.Rename(name);
        unit.SetMaxOccupancy(request.MaxOccupancy);
        unit.SetBasePrice(request.BasePrice);
        unit.SetCurrency(request.Currency);
        unit.SetCancellationPolicy(CancellationPolicy.Create(request.CancellationTiers));

        await dbContext.SaveChangesAsync(cancellationToken);

        return new UpdateUnitResponse { UnitId = unit.Id };
    }
}
