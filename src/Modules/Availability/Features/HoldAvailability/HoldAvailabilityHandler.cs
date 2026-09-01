using Ardalis.GuardClauses;
using Availability.Exceptions;
using BuildingBlocks.Time;
using Catalog.Contracts;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using NotFoundException = BuildingBlocks.Exceptions.NotFoundException;

namespace Availability.Features.HoldAvailability;

public class HoldAvailabilityHandler(
    AppAvailabilityDbContext dbContext,
    IUnitLookup unitLookup,
    TimeProvider timeProvider,
    IOptions<HoldCapOptions> holdCapOptions)
    : IRequestHandler<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    // Lives here, not the validator, since it needs "today" - same
    // reasoning as the CheckIn-in-the-past guard below. Without it an
    // anonymous caller could hold a unit for [today, today+3650), and the
    // exclusion constraint would enforce that block for a decade.
    private const int MaxLeadTimeDays = 730;

    // Live holds one client network may have at once, counted by
    // ClientKey. This used to be 5 per hold-session cookie, which was no
    // cap at all: the cookie is client-supplied, so discarding it minted a
    // fresh budget per request.
    //
    // It's here rather than in the "holds" rate-limit policy because the
    // two bound different things and only this one bounds the resource
    // that matters. A fixed-window limiter caps request *rate*; holds
    // expire on their own 15-minute clock, so at 20/60s a single caller
    // accumulates ~300 concurrent live holds without ever tripping it -
    // each blocking up to MaxStayNights of a unit via the exclusion
    // constraint. Rate says how fast you reach saturation, not how much
    // you can hold. See docs/adr/0016.
    private readonly int _maxActiveHoldsPerClient = holdCapOptions.Value.MaxActiveHoldsPerClient;

    public async ValueTask<HoldAvailabilityResponse> Handle(
        HoldAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        // One call covers everything this module needs from Catalog: the
        // unit's max occupancy (for the guard below) and the resolved price
        // for this stay - see IUnitLookup's own doc comment for why this is
        // one round trip instead of two separate EF reads (Units, then
        // PricingRules).
        StayPricingResult pricing = await unitLookup.ResolveStayPricingAsync(
                                        request.UnitId, request.CheckIn, request.CheckOut, cancellationToken)
                                    ?? throw new NotFoundException("Unit", request.UnitId);

        // Guard clauses for invariants that depend on THIS unit's data -
        // the request validator already confirmed CheckOut > CheckIn and
        // GuestCount > 0 as pure shape rules; these need the loaded Unit.
        Guard.Against.OutOfRange(
            request.GuestCount, nameof(request.GuestCount), 1, pricing.MaxOccupancy,
            $"Guest count exceeds this unit's maximum occupancy of {pricing.MaxOccupancy}.");

        // The property's own zone, from the pricing lookup already awaited
        // above - not UTC. At UTC+3 a UTC "today" lags local and accepts
        // check-ins already in the past; west of UTC it runs ahead and
        // rejects valid same-day bookings. See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, pricing.TimeZoneId);

        Guard.Against.InvalidInput(
            request.CheckIn, nameof(request.CheckIn),
            d => d >= today,
            "Check-in date cannot be in the past.");

        Guard.Against.InvalidInput(
            request.CheckIn, nameof(request.CheckIn),
            d => d.DayNumber - today.DayNumber <= MaxLeadTimeDays,
            $"Check-in date cannot be more than {MaxLeadTimeDays} days in the future.");

        // Wrapped in the execution strategy, not called bare - a manually
        // started transaction bypasses EF's per-operation retry wrapping,
        // which would otherwise surface a deadlock (40P01) as an unhandled
        // 500 instead of retrying. See docs/adr/0010 for why this happens
        // under concurrent contention on the same range, not just in
        // theory.
        //
        // Serializable, not Read Committed, for the same reason
        // CreatePricingRuleHandler/UpdatePricingRuleHandler need it
        // (docs/adr/0012): the hold cap below is a COUNT-then-INSERT
        // against a shared predicate (client_key = @ClientKey), and under
        // Read Committed, N concurrent holds from the same client on N
        // different units can all COUNT before any commits its own
        // INSERT, oversubscribing the cap - proven empirically via
        // HoldAvailabilityConcurrencyTests, which measured 9 successful
        // holds against a cap of 5 before this was added. That matters
        // more now than it did when the key was the cookie: a caller who
        // wants to exceed the cap can no longer just discard the key, so
        // racing it is the remaining way to try. No EF
        // change-tracking risk here on retry - this transaction is pure
        // Dapper, so every retry re-issues a real SQL round trip with no
        // identity map to go stale.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        (Guid HoldId, DateTimeOffset HoldExpiresAt) result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            DateTimeOffset now = timeProvider.GetUtcNow();

            // Stale holds from abandoned checkouts otherwise sit in 'held'
            // forever, permanently occupying their slot in the exclusion
            // constraint below even though GetPriceCalendarHandler already
            // treats them as available. Scoped to this unit and run right
            // before the INSERT they'd actually block.
            const string cleanupSql = """
                                      DELETE FROM unit_availability_holds
                                      WHERE unit_id = @UnitId AND status = 'held' AND hold_expires_at <= @Now;
                                      """;

            await connection.ExecuteAsync(new CommandDefinition(
                cleanupSql,
                new { request.UnitId, Now = now },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            // Counts this client network's live holds across every unit.
            // 'booked' is deliberately excluded: ConfirmHoldAsync
            // sets it and nothing ever clears it (the reconciliation job
            // depends on that persistence), so counting it would mean a
            // customer permanently loses hold capacity after their Nth
            // successful booking. Expired 'held' rows are excluded too -
            // the cleanup DELETE above is scoped to this unit only, so a
            // stale hold elsewhere would otherwise count until
            // ExpiredHoldsSweepJob reaps it.
            const string activeHoldCountSql = """
                                              SELECT count(*) FROM unit_availability_holds
                                              WHERE client_key = @ClientKey AND status = 'held' AND hold_expires_at > @Now;
                                              """;

            int activeHoldCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                activeHoldCountSql,
                new { request.ClientKey, Now = now },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (activeHoldCount >= _maxActiveHoldsPerClient)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new TooManyActiveHoldsException();
            }

            Guid holdId = Guid.CreateVersion7();
            DateTimeOffset holdExpiresAt = now.Add(HoldDuration);

            const string sql = """
                               INSERT INTO unit_availability_holds (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, subtotal, currency, length_of_stay_discount_amount, holder_token, client_key)
                               VALUES (@Id, @UnitId, @StayRange, 'held', @HoldExpiresAt, @CreatedAt, @GuestCount, @TotalPrice, @Subtotal, @Currency, @LengthOfStayDiscountAmount, @HolderToken, @ClientKey);
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
                        TotalPrice = pricing.TotalPrice.Amount,
                        Subtotal = pricing.Subtotal,
                        Currency = pricing.TotalPrice.Currency.ToString(),
                        LengthOfStayDiscountAmount = pricing.LengthOfStayDiscountAmount?.Amount,
                        request.HolderToken,
                        request.ClientKey
                    },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
            {
                // The database itself rejected the insert - some or all of
                // the requested range is already held/booked for this unit.
                // This IS the double-booking guarantee: no rows-affected
                // check, no manual locking, the constraint does the work.
                // Not classified as transient, so it propagates straight
                // out of ExecuteAsync instead of being retried - a real
                // conflict, not a transient one. See
                // HoldAvailabilityConcurrencyTests for proof this holds
                // under real concurrent requests.
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
            TotalPrice = pricing.TotalPrice.Amount,
            Currency = pricing.TotalPrice.Currency
        };
    }
}
