using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Catalog.Enums;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.UpdatePricingRule;

public class UpdatePricingRuleHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization) : IRequestHandler<UpdatePricingRuleRequest, UpdatePricingRuleResponse>
{
    public async ValueTask<UpdatePricingRuleResponse> Handle(
        UpdatePricingRuleRequest request, CancellationToken cancellationToken)
    {
        PricingRule? rule = await dbContext.PricingRules
            .SingleOrDefaultAsync(r => r.Id == request.PricingRuleId, cancellationToken);

        // Not found if the id doesn't exist, OR it exists but belongs to a
        // different unit than the URL claims - same "don't leak existence"
        // reasoning IHostAuthorization.RequireOwnership already uses.
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

        if (request.RuleType != rule.RuleType)
        {
            throw new ValidationException(nameof(request.RuleType), "RuleType cannot be changed once a rule is created.");
        }

        // Same TOCTOU this ADR-0012/#9 fix closed on the create path -
        // CreatePricingRuleHandler's own comment covers the full reasoning
        // (still no GIST constraint, matching ADR-0012's low-frequency/
        // single-host reasoning). No ChangeTracker.Clear() here, unlike
        // Create: `rule` is already loaded and tracked above (needed for
        // the ownership check before this point), and re-running the
        // ApplyXxx setter calls on a retry is idempotent - they overwrite
        // with the same request-derived values, they don't accumulate -
        // so there's nothing a cleared tracker would need to protect
        // against here.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            List<PricingRule> existingSameType = await dbContext.PricingRules
                .Where(r => r.UnitId == rule.UnitId && r.RuleType == rule.RuleType && r.Id != rule.Id)
                .ToListAsync(cancellationToken);

            switch (rule.RuleType)
            {
                case PricingRuleType.DateRangeOverride:
                    ApplyDateRangeOverride(rule, request, existingSameType);
                    break;
                case PricingRuleType.DayOfWeekMultiplier:
                    ApplyDayOfWeekMultiplier(rule, request, existingSameType);
                    break;
                case PricingRuleType.LengthOfStayDiscount:
                    ApplyLengthOfStayDiscount(rule, request, existingSameType);
                    break;
                default:
                    throw new ValidationException(nameof(request.RuleType), "Unsupported RuleType.");
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new UpdatePricingRuleResponse { PricingRuleId = rule.Id };
    }

    private static void ApplyDateRangeOverride(PricingRule rule, UpdatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        DateOnly startDate = request.StartDate!.Value;
        DateOnly endDate = request.EndDate!.Value;
        PricingRuleOverlapChecker.EnsureNoDateRangeConflict(startDate, endDate, existing);

        rule.SetDateRange(startDate, endDate);
        rule.SetOverridePrice(request.OverridePrice!.Value);
    }

    private static void ApplyDayOfWeekMultiplier(PricingRule rule, UpdatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        int[] daysOfWeek = request.DaysOfWeek!;
        PricingRuleOverlapChecker.EnsureNoDayOfWeekConflict(daysOfWeek, existing);

        rule.SetDaysOfWeek(daysOfWeek);
        rule.SetMultiplier(request.Multiplier!.Value);
    }

    private static void ApplyLengthOfStayDiscount(PricingRule rule, UpdatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        PricingRuleOverlapChecker.EnsureNoLengthOfStayConflict(existing);

        rule.SetMinNights(request.MinNights!.Value);
        rule.SetDiscountPercent(request.DiscountPercent!.Value);
    }
}
