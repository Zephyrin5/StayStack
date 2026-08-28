using Ardalis.GuardClauses;
using Availability.Exceptions;
using Catalog.Contracts;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using NotFoundException = BuildingBlocks.Exceptions.NotFoundException;

namespace Availability.Features.HoldAvailability;

public class HoldAvailabilityHandler(AppAvailabilityDbContext dbContext, IUnitLookup unitLookup, TimeProvider timeProvider)
    : IRequestHandler<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    // Lives here, not the validator, since it needs "today" - same
    // reasoning as the existing CheckIn-in-the-past guard just below.
    // Without it an anonymous caller could hold a unit for [today,
    // today+3650) and the exclusion constraint would faithfully enforce
    // that block for a decade.
    private const int MaxLeadTimeDays = 730;

    // Concurrent active (held/booked) holds one hold-session token may
    // accumulate at once. Deliberately soft - a scripted caller dropping
    // the cookie mints a fresh token per request and sails past this - see
    // docs/adr/0016 for what actually bounds the abuse case (the stay-
    // length/lead-time caps above and the "holds" rate-limit policy).
    private const int MaxActiveHoldsPerSession = 5;

    public async ValueTask<HoldAvailabilityResponse> Handle(
        HoldAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        // One call covers everything this module needs from Catalog: the
        // unit's max occupancy (for the guard below) and the resolved
        // price for this exact stay - see Catalog.Contracts.IUnitLookup's
        // own doc comment for why this replaced two local EF reads
        // (Units, then PricingRules) this handler used to do directly,
        // back when it lived in the same module as both tables.
        StayPricingResult pricing = await unitLookup.ResolveStayPricingAsync(
                                        request.UnitId, request.CheckIn, request.CheckOut, cancellationToken)
                                    ?? throw new NotFoundException("Unit", request.UnitId);

        // Guard clauses for invariants that depend on THIS unit's data -
        // the request validator already confirmed CheckOut > CheckIn and
        // GuestCount > 0 as pure shape rules; these need the loaded Unit.
        Guard.Against.OutOfRange(
            request.GuestCount, nameof(request.GuestCount), 1, pricing.MaxOccupancy,
            $"Guest count exceeds this unit's maximum occupancy of {pricing.MaxOccupancy}.");

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().DateTime);

        Guard.Against.InvalidInput(
            request.CheckIn, nameof(request.CheckIn),
            d => d >= today,
            "Check-in date cannot be in the past.");

        Guard.Against.InvalidInput(
            request.CheckIn, nameof(request.CheckIn),
            d => d.DayNumber - today.DayNumber <= MaxLeadTimeDays,
            $"Check-in date cannot be more than {MaxLeadTimeDays} days in the future.");

        // Wrapped in the execution strategy, not called bare - a manually
        // started transaction bypasses EF's own per-operation retry
        // wrapping, which would otherwise surface a deadlock (40P01) as an
        // unhandled 500 instead of retrying the whole transaction from
        // scratch. See docs/adr/0010 for why this actually happens under
        // concurrent contention on the same range, not just in theory.
        //
        // Serializable, not the default Read Committed, for the same
        // reason CreatePricingRuleHandler/UpdatePricingRuleHandler need it
        // (docs/adr/0012): the per-session cap below is a COUNT-then-INSERT
        // against a shared predicate (holder_token = @HolderToken), and
        // under Read Committed, N concurrent holds from the same session on
        // N different (non-conflicting) units can all COUNT before any of
        // them commits their own INSERT, oversubscribing the cap - proven
        // empirically via HoldAvailabilityConcurrencyTests
        // (Hold_ConcurrentRequestsSharingTheSameHolderToken_
        // NeverExceedTheSessionCap), which measured 9 successful holds
        // against a cap of 5 before this was added. No EF change-tracking
        // risk on retry here the way UpdatePricingRuleHandler had - this
        // transaction is pure Dapper, so every retry re-issues a real SQL
        // round trip with no client-side identity map to go stale.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        (Guid HoldId, DateTimeOffset HoldExpiresAt) result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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

            // Soft cap, not the real defense (see the class-level doc
            // comment on MaxActiveHoldsPerSession) - counts this session's
            // own still-active holds across every unit, not just this one.
            // 'booked' is deliberately excluded: ConfirmHoldAsync sets it
            // and nothing ever clears it for a successfully completed
            // booking (the reconciliation job depends on exactly that
            // persistence) - counting it here would mean a real customer
            // permanently loses hold capacity on this browser after their
            // Nth successful booking, ever. Expired 'held' rows are
            // excluded too - the inline cleanup DELETE above is scoped to
            // this unit only, so a stale hold on a different unit would
            // otherwise count against this session until
            // ExpiredHoldsSweepJob happens to reap it.
            const string activeHoldCountSql = """
                                              SELECT count(*) FROM unit_availability_holds
                                              WHERE holder_token = @HolderToken AND status = 'held' AND hold_expires_at > @Now;
                                              """;

            int activeHoldCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                activeHoldCountSql,
                new { request.HolderToken, Now = now },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (activeHoldCount >= MaxActiveHoldsPerSession)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new TooManyActiveHoldsException();
            }

            Guid holdId = Guid.CreateVersion7();
            DateTimeOffset holdExpiresAt = now.Add(HoldDuration);

            const string sql = """
                               INSERT INTO unit_availability_holds (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, subtotal, currency, length_of_stay_discount_amount, holder_token)
                               VALUES (@Id, @UnitId, @StayRange, 'held', @HoldExpiresAt, @CreatedAt, @GuestCount, @TotalPrice, @Subtotal, @Currency, @LengthOfStayDiscountAmount, @HolderToken);
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
                        request.HolderToken
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
            TotalPrice = pricing.TotalPrice.Amount,
            Currency = pricing.TotalPrice.Currency
        };
    }
}
