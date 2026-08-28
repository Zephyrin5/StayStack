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
        // Create, and deliberately not - `rule` was loaded once, before the
        // retry strategy even starts (needed for the ownership check
        // above), and stays the SAME tracked instance across every retry;
        // clearing it would detach it, silently breaking SaveChangesAsync's
        // ability to see rule.SetDateRange/SetOverridePrice's mutations at
        // all on a retried attempt.
        //
        // AsNoTracking() below is the actual fix a previous version of
        // this comment claimed wasn't needed. existingSameType IS a fresh
        // query every retry, but without AsNoTracking(), EF's identity map
        // returns whatever instance of a sibling row is ALREADY tracked
        // from a prior (rolled-back) attempt - with that attempt's stale,
        // pre-conflict property values - instead of the row a repeated
        // SELECT just actually fetched. A retry after a genuine 40001
        // would silently keep checking a sibling's OLD state, defeating
        // the entire point of retrying under Serializable isolation.
        // Confirmed empirically via PricingRuleConcurrencyTests' write-skew
        // case: without this, the losing side's retry incorrectly
        // succeeded against a sibling's already-superseded range. Safe to
        // add - existingSameType is read-only data for the overlap check
        // below, never mutated, so it has no business touching the
        // tracker/identity map in the first place.
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
