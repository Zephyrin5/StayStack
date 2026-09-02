using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Exceptions;
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
        // CreatePricingRuleHandler's own comment covers the full reasoning.
        // No ChangeTracker.Clear() here, unlike Create, and deliberately
        // not - `rule` was loaded once, before the retry strategy starts,
        // and stays the SAME tracked instance across every retry; clearing
        // it would detach it, breaking SaveChangesAsync's ability to see
        // rule.SetDateRange/SetOverridePrice's mutations on a retried
        // attempt.
        //
        // AsNoTracking() below IS required, despite existingSameType being
        // a fresh query every retry: without it, EF's identity map returns
        // whatever sibling-row instance is already tracked from a prior
        // rolled-back attempt - with THAT attempt's stale, pre-conflict
        // values - instead of what the repeated SELECT just fetched. A
        // retry after a genuine 40001 would silently keep checking a
        // sibling's old state, defeating the point of retrying under
        // Serializable isolation. Confirmed empirically via
        // PricingRuleConcurrencyTests' write-skew case. Safe to add -
        // existingSameType is read-only here, never mutated.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            List<PricingRule> existingSameType = await dbContext.PricingRules
                .AsNoTracking()
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

            // The in-memory checks above catch this first in the ordinary
            // case; the constraints catch the interleaving where two
            // transactions each pass their own check. Same conflict either
            // way, so the caller sees the same 409 - see
            // PricingRuleOverlapChecker.IsOverlapViolation.
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (PricingRuleOverlapChecker.IsOverlapViolation(exception, out string conflict))
            {
                throw new PricingRuleConflictException(conflict);
            }

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
