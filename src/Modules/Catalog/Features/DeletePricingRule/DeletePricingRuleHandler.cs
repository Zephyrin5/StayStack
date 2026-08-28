using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.DeletePricingRule;

public class DeletePricingRuleHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    TimeProvider timeProvider) : IRequestHandler<DeletePricingRuleRequest, DeletePricingRuleResponse>
{
    public async ValueTask<DeletePricingRuleResponse> Handle(
        DeletePricingRuleRequest request, CancellationToken cancellationToken)
    {
        PricingRule? rule = await dbContext.PricingRules
            .SingleOrDefaultAsync(r => r.Id == request.PricingRuleId, cancellationToken);

        if (rule is null || rule.UnitId != request.UnitId)
        {
            throw new NotFoundException(nameof(PricingRule), request.PricingRuleId);
        }

        Unit? unit = await dbContext.Units
            .SingleOrDefaultAsync(u => u.Id == rule.UnitId, cancellationToken);

        if (unit is null)
        {
            throw new NotFoundException(nameof(Unit), rule.UnitId);
        }

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

        rule.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeletePricingRuleResponse { PricingRuleId = rule.Id };
    }
}
