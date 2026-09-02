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

        // ADR-0012 deliberately skips a GIST exclusion constraint here
        // (low-frequency, single-host action, unlike guests racing for a
        // unit) - but the check-then-insert below is still a genuine
        // read-then-write race with nothing at the database enforcing it.
        // Serializable isolation closes that without contradicting
        // ADR-0012: still no GIST constraint, just a transaction strong
        // enough that two concurrent conflicting inserts can't both pass
        // their own overlap check. A losing transaction fails with
        // Postgres' 40001, which EnableRetryOnFailure is configured to
        // retry rather than surface as a 500. ChangeTracker.Clear() is
        // required here (unlike HoldAvailabilityHandler's raw-Dapper
        // transaction) - this one calls dbContext.Add(), and a retried
        // delegate would otherwise re-add a second entity on top of the
        // first attempt's still-tracked, rolled-back one.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        Guid pricingRuleId = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

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

            return rule.Id;
        });

        return new CreatePricingRuleResponse { PricingRuleId = pricingRuleId };
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
