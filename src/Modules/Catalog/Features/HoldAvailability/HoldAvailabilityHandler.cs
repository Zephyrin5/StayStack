using Ardalis.GuardClauses;
using Catalog.Domain;
using Catalog.Entities;
using Catalog.Exceptions;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using NotFoundException = BuildingBlocks.Exceptions.NotFoundException;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.HoldAvailability;

public class HoldAvailabilityHandler(AppCatalogDbContext dbContext, TimeProvider timeProvider)
    : IRequestHandler<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    public async ValueTask<HoldAvailabilityResponse> Handle(
        HoldAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        Unit unit = await dbContext.Units
                        .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken)
                    ?? throw new NotFoundException(nameof(Unit), request.UnitId);

        List<PricingRule> rules = await dbContext.PricingRules
            .Where(r => r.UnitId == unit.Id)
            .ToListAsync(cancellationToken);

        // Guard clauses for invariants that depend on THIS unit's data -
        // the request validator already confirmed CheckOut > CheckIn and
        // GuestCount > 0 as pure shape rules; these need the loaded Unit.
        Guard.Against.OutOfRange(
            request.GuestCount, nameof(request.GuestCount), 1, unit.MaxOccupancy,
            $"Guest count exceeds this unit's maximum occupancy of {unit.MaxOccupancy}.");

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().DateTime);

        Guard.Against.InvalidInput(
            request.CheckIn, nameof(request.CheckIn),
            d => d >= today,
            "Check-in date cannot be in the past.");

        int nights = request.CheckOut.DayNumber - request.CheckIn.DayNumber;
        decimal totalPrice = PricingCalculator.ResolveStayTotal(
            unit.BasePrice, request.CheckIn, request.CheckOut, rules);

        // Wrapped in the execution strategy, not called bare - a manually
        // started transaction bypasses EF's own per-operation retry
        // wrapping, which would otherwise surface a deadlock (40P01) as an
        // unhandled 500 instead of retrying the whole transaction from
        // scratch. See docs/adr/0010 for why this actually happens under
        // concurrent contention on the same range, not just in theory.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        (Guid HoldId, DateTimeOffset HoldExpiresAt) result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            DateTimeOffset now = timeProvider.GetUtcNow();

            // Stale holds from abandoned checkouts otherwise sit in 'held'
            // forever (nothing else ever transitions them out), permanently
            // occupying their slot in the exclusion constraint below even
            // though GetPriceCalendarHandler already treats them as available.
            // Scoped to this unit and run right before the INSERT that would
            // actually be blocked by them - the one case where a stale row's
            // presence matters.
            const string cleanupSql = """
                                      DELETE FROM unit_availability_holds
                                      WHERE unit_id = @UnitId AND status = 'held' AND hold_expires_at <= @Now;
                                      """;

            await connection.ExecuteAsync(new CommandDefinition(
                cleanupSql,
                new { request.UnitId, Now = now },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            Guid holdId = Guid.CreateVersion7();
            DateTimeOffset holdExpiresAt = now.Add(HoldDuration);

            const string sql = """
                               INSERT INTO unit_availability_holds (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, currency)
                               VALUES (@Id, @UnitId, @StayRange, 'held', @HoldExpiresAt, @CreatedAt, @GuestCount, @TotalPrice, @Currency);
                               """;

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        Id = holdId,
                        request.UnitId,
                        // Half-open range [CheckIn, CheckOut) - checkout day
                        // itself is not occupied, matching normal hospitality
                        // date semantics.
                        StayRange = new NpgsqlRange<DateOnly>(request.CheckIn, true, request.CheckOut, false),
                        HoldExpiresAt = holdExpiresAt,
                        CreatedAt = now,
                        request.GuestCount,
                        TotalPrice = totalPrice,
                        Currency = unit.Currency.ToString()
                    },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
            {
                // The database itself rejected the insert - some or all of
                // the requested range is already held/booked for this
                // unit. This IS the double-booking guarantee: no rows-
                // affected check, no manual locking, the constraint does
                // the work. Not classified as transient (it isn't a
                // PostgresException the execution strategy retries), so it
                // propagates straight out of ExecuteAsync instead of being
                // retried - a real conflict, not a transient one. See
                // HoldAvailabilityConcurrencyTests for proof this actually
                // holds under real concurrent requests.
                await transaction.RollbackAsync(cancellationToken);
                throw new UnitUnavailableException(request.UnitId);
            }

            await transaction.CommitAsync(cancellationToken);

            return (holdId, holdExpiresAt);
        });

        return new HoldAvailabilityResponse
        {
            HoldId = result.HoldId,
            HoldExpiresAt = result.HoldExpiresAt.UtcDateTime,
            TotalPrice = totalPrice,
            Currency = unit.Currency
        };
    }
}
