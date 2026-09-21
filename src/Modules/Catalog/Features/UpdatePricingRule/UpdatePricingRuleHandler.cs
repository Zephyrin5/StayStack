using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Exceptions;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.UpdatePricingRule;

public class UpdatePricingRuleHandler(
    CatalogDb dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization) : IRequestHandler<UpdatePricingRuleRequest, UpdatePricingRuleResponse>
{
    public async ValueTask<UpdatePricingRuleResponse> Handle(
        UpdatePricingRuleRequest request, CancellationToken cancellationToken)
    {
        // Loaded for the checks below only - the retried delegate reloads it.
        PricingRule? rule = await dbContext.PricingRules.AsNoTracking()
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

        // Same race as the create path, decided the same way: by the schema's
        // overlap constraints, not by Serializable - see
        // CreatePricingRuleHandler's comment. Two updates that each conflict
        // only with the other's new state are caught by the constraint on
        // whichever commits second.
        //
        // ChangeTracker.Clear() and a reload inside the delegate, with the
        // mutations applied to the fresh instance. SaveChangesAsync accepts its
        // changes when it returns (acceptAllChangesOnSuccess defaults to true),
        // so a tracked instance kept across attempts has the new price as its
        // original value before CommitAsync runs. A transient commit failure
        // would retry a delegate that re-applies values EF no longer sees as
        // changes: no UPDATE, commit succeeds, caller gets 200, row unchanged
        // (docs/adr/0025).
        //
        // AsNoTracking() below is for a read-only query.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Reloaded under this attempt's transaction. The instance resolved
            // above belongs to the authorization checks and to a snapshot a
            // previous attempt may already have changed.
            PricingRule locked = await dbContext.PricingRules
                                     .SingleOrDefaultAsync(r => r.Id == request.PricingRuleId, cancellationToken)
                                 ?? throw new NotFoundException(nameof(PricingRule), request.PricingRuleId);

            List<PricingRule> existingSameType = await dbContext.PricingRules
                .AsNoTracking()
                .Where(r => r.UnitId == locked.UnitId && r.RuleType == locked.RuleType && r.Id != locked.Id)
                .ToListAsync(cancellationToken);

            switch (locked.RuleType)
            {
                case PricingRuleType.DateRangeOverride:
                    ApplyDateRangeOverride(locked, request, existingSameType);
                    break;
                case PricingRuleType.DayOfWeekMultiplier:
                    ApplyDayOfWeekMultiplier(locked, request, existingSameType);
                    break;
                case PricingRuleType.LengthOfStayDiscount:
                    ApplyLengthOfStayDiscount(locked, request, existingSameType);
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
            catch (Exception exception) when (PricingRuleOverlapChecker.IsOverlapViolation(exception, locked, out string conflict))
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
        PricingRuleOverlapChecker.EnsureNoLengthOfStayConflict(request.MinNights!.Value, existing);

        rule.SetMinNights(request.MinNights.Value);
        rule.SetDiscountPercent(request.DiscountPercent!.Value);
    }
}
