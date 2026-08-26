using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.ListPricingRules;

public class ListPricingRulesHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization) : IRequestHandler<ListPricingRulesRequest, ListPricingRulesResponse>
{
    public async ValueTask<ListPricingRulesResponse> Handle(
        ListPricingRulesRequest request, CancellationToken cancellationToken)
    {
        Unit? unit = await dbContext.Units
            .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken);

        if (unit is null)
        {
            throw new NotFoundException(nameof(Unit), request.UnitId);
        }

        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == unit.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), unit.PropertyId);
        }

        if (!currentUserProvider.Roles.Contains("Administrator"))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        List<PricingRule> rules = await dbContext.PricingRules
            .AsNoTracking()
            .Where(r => r.UnitId == request.UnitId)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return new ListPricingRulesResponse
        {
            Rules = rules.Select(r => new PricingRuleSummary
            {
                PricingRuleId = r.Id,
                RuleType = r.RuleType,
                StartDate = r.DateRange?.LowerBound,
                EndDate = r.DateRange?.UpperBound,
                OverridePrice = r.OverridePrice,
                DaysOfWeek = r.DaysOfWeek,
                Multiplier = r.Multiplier,
                MinNights = r.MinNights,
                DiscountPercent = r.DiscountPercent
            }).ToList()
        };
    }
}
