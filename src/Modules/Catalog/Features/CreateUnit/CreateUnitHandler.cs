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

namespace Catalog.Features.CreateUnit;

public class CreateUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<CreateUnitRequest, CreateUnitResponse>
{
    public async ValueTask<CreateUnitResponse> Handle(CreateUnitRequest request, CancellationToken cancellationToken)
    {
        // Unlike the previous AnyAsync existence check, this needs the
        // full entity now - HostId is required to verify ownership below.
        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), request.PropertyId);
        }

        // Administrators may create a Unit under any Property; a Host may
        // only do so under their own. Safe to branch on role here,
        // unlike CreatePropertyRequest's old HostId field - this decides
        // whether to run an extra check, not whether to trust
        // client-supplied data over the token.
        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), request.PropertyId);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        // null (omitted) means "use Unit.Create's own default" - only build
        // one from the request when tiers were actually provided.
        CancellationPolicy? cancellationPolicy = request.CancellationTiers is not null
            ? CancellationPolicy.Create(request.CancellationTiers)
            : null;

        Unit unit = Unit.Create(
            request.PropertyId,
            name,
            request.MaxOccupancy,
            request.BasePrice,
            request.Currency,
            cancellationPolicy);

        dbContext.Units.Add(unit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateUnitResponse { UnitId = unit.Id };
    }
}
