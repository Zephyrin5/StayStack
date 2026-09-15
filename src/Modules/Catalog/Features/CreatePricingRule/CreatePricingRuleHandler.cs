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

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        // The in-memory overlap check below is a read-then-insert, and two
        // concurrent creates can both pass it. The database decides that race,
        // not the isolation level: all three overlap invariants are schema
        // constraints (docs/adr/0012), and the loser's violation is translated
        // into the same 409 by IsOverlapViolation. Serializable would add a
        // 40001 retry on ordinary contention and guard nothing further.
        //
        // ChangeTracker.Clear() is required - this calls dbContext.Add(), and a
        // retried delegate would otherwise re-add a second entity on top of the
        // first attempt's still-tracked, rolled-back one.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        // Once, outside the retry (docs/adr/0025). A fresh id per attempt would
        // make a retry after a lost acknowledgement meet the first attempt's
        // committed rule in the overlap check and report the host's own new rule
        // as a conflict.
        Guid pricingRuleId = Guid.CreateVersion7();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // An earlier attempt may already have committed. Asked before the
            // overlap check, because that check would otherwise judge the
            // request against a set containing its own rule and refuse it - the
            // same ordering mistake initiation and the hold cap each made.
            if (await dbContext.PricingRules.AsNoTracking().AnyAsync(r => r.Id == pricingRuleId, cancellationToken))
            {
                return;
            }

            List<PricingRule> existingSameType = await dbContext.PricingRules
                .Where(r => r.UnitId == request.UnitId && r.RuleType == request.RuleType)
                .ToListAsync(cancellationToken);

            PricingRule rule = request.RuleType switch
            {
                PricingRuleType.DateRangeOverride => CreateDateRangeOverride(pricingRuleId, request, existingSameType),
                PricingRuleType.DayOfWeekMultiplier => CreateDayOfWeekMultiplier(pricingRuleId, request, existingSameType),
                PricingRuleType.LengthOfStayDiscount => CreateLengthOfStayDiscount(pricingRuleId, request, existingSameType),
                _ => throw new ValidationException(nameof(request.RuleType), "Unsupported RuleType.")
            };

            dbContext.PricingRules.Add(rule);
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

        return new CreatePricingRuleResponse { PricingRuleId = pricingRuleId };
    }

    private static PricingRule CreateDateRangeOverride(
        Guid id, CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        DateOnly startDate = request.StartDate!.Value;
        DateOnly endDate = request.EndDate!.Value;
        PricingRuleOverlapChecker.EnsureNoDateRangeConflict(startDate, endDate, existing);

        return PricingRule.CreateDateRangeOverride(id, request.UnitId, startDate, endDate, request.OverridePrice!.Value);
    }

    private static PricingRule CreateDayOfWeekMultiplier(
        Guid id, CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        int[] daysOfWeek = request.DaysOfWeek!;
        PricingRuleOverlapChecker.EnsureNoDayOfWeekConflict(daysOfWeek, existing);

        return PricingRule.CreateDayOfWeekMultiplier(id, request.UnitId, daysOfWeek, request.Multiplier!.Value);
    }

    private static PricingRule CreateLengthOfStayDiscount(
        Guid id, CreatePricingRuleRequest request, IReadOnlyList<PricingRule> existing)
    {
        PricingRuleOverlapChecker.EnsureNoLengthOfStayConflict(existing);

        return PricingRule.CreateLengthOfStayDiscount(id, request.UnitId, request.MinNights!.Value, request.DiscountPercent!.Value);
    }
}
