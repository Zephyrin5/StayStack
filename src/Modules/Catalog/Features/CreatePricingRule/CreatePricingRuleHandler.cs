using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Catalog.Enums;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.CreatePricingRule;

public class CreatePricingRuleHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization) : IRequestHandler<CreatePricingRuleRequest, CreatePricingRuleResponse>
{
    public async ValueTask<CreatePricingRuleResponse> Handle(
        CreatePricingRuleRequest request, CancellationToken cancellationToken)
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

        if (!currentUserProvider.Roles.Contains("Administrator"))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        List<PricingRule> existingSameType = await dbContext.PricingRules
            .Where(r => r.UnitId == request.UnitId && r.RuleType == request.RuleType)
            .ToListAsync(cancellationToken);

        PricingRule rule = request.RuleType switch
        {
            PricingRuleType.DateRangeOverride => CreateDateRangeOverride(request, existingSameType),
            PricingRuleType.DayOfWeekMultiplier => CreateDayOfWeekMultiplier(request, existingSameType),
            PricingRuleType.LengthOfStayDiscount => CreateLengthOfStayDiscount(request, existingSameType),
            _ => throw new ValidationException(nameof(request.RuleType), "Unsupported RuleType.")
        };

        dbContext.PricingRules.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreatePricingRuleResponse { PricingRuleId = rule.Id };
    }

    private static PricingRule CreateDateRangeOverride(CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        DateOnly startDate = request.StartDate!.Value;
        DateOnly endDate = request.EndDate!.Value;
        PricingRuleOverlapChecker.EnsureNoDateRangeConflict(startDate, endDate, existing);

        return PricingRule.CreateDateRangeOverride(request.UnitId, startDate, endDate, request.OverridePrice!.Value);
    }

    private static PricingRule CreateDayOfWeekMultiplier(CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        int[] daysOfWeek = request.DaysOfWeek!;
        PricingRuleOverlapChecker.EnsureNoDayOfWeekConflict(daysOfWeek, existing);

        return PricingRule.CreateDayOfWeekMultiplier(request.UnitId, daysOfWeek, request.Multiplier!.Value);
    }

    private static PricingRule CreateLengthOfStayDiscount(CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        PricingRuleOverlapChecker.EnsureNoLengthOfStayConflict(existing);

        return PricingRule.CreateLengthOfStayDiscount(request.UnitId, request.MinNights!.Value, request.DiscountPercent!.Value);
    }
}
