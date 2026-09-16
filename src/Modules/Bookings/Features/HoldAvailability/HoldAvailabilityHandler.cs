using Bookings.Entities.Configurations;
using Persistence;
using Bookings.Entities;
using Bookings.Exceptions;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Persistence;
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

namespace Bookings.Features.HoldAvailability;

public class HoldAvailabilityHandler(
    BookingsDb dbContext,
    IUnitLookup unitLookup,
    TimeProvider timeProvider,
    IOptions<HoldCapOptions> holdCapOptions,
    IOptions<StaySearchPolicyOptions> staySearchPolicy)
    : IRequestHandler<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    // Enforced here, not in the validator, since it needs "today" - same
    // reasoning as the CheckIn-in-the-past guard below. Without it an
    // anonymous caller could hold a unit for [today, today+3650), and the
    // exclusion constraint would enforce that block for a decade.
    //
    // Shared with GetPropertiesHandler rather than defined here: a search
    // that offers dates this handler then refuses is a dead end the guest
    // only reaches after picking them. See StaySearchPolicyOptions.
    private readonly int _maxLeadTimeDays = staySearchPolicy.Value.MaxLeadTimeDays;

    // Live holds one client network may have at once, counted by ClientKey,
    // which the caller cannot choose. The "holds" rate limit bounds request
    // rate; holds expire on their own 15-minute clock, so a caller within the
    // rate can still accumulate hundreds of live holds, each blocking up to
    // StaySearchPolicyOptions.MaxStayNights of a unit. This cap bounds the
    // stock (docs/adr/0016).
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

        // Checks for invariants that depend on THIS unit's data - the request
        // validator already confirmed CheckOut > CheckIn and GuestCount > 0 as
        // pure shape rules; these need the loaded Unit.
        //
        // ValidationException, not Guard.Against.*, at all three sites below:
        // these reject caller input, so they need a 400 with a message written
        // for the caller. A guard's ArgumentException is a 500 by design (see
        // GlobalExceptionHandler), and its BCL message names parameters.
        if (request.GuestCount > pricing.MaxOccupancy)
        {
            throw new ValidationException(
                nameof(request.GuestCount),
                $"Guest count exceeds this unit's maximum occupancy of {pricing.MaxOccupancy}.");
        }

        // The property's own zone, from the pricing lookup already awaited
        // above - not UTC. At UTC+3 a UTC "today" lags local and accepts
        // check-ins already in the past; west of UTC it runs ahead and
        // rejects valid same-day bookings. See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, pricing.TimeZoneId);

        if (request.CheckIn < today)
        {
            throw new ValidationException(nameof(request.CheckIn), "Check-in date cannot be in the past.");
        }

        if (request.CheckIn.DayNumber - today.DayNumber > _maxLeadTimeDays)
        {
            throw new ValidationException(
                nameof(request.CheckIn),
                $"Check-in date cannot be more than {_maxLeadTimeDays} days in the future.");
        }

        // In the execution strategy: a manually started transaction bypasses EF's
        // per-operation retry, and holds on one range deadlock under contention
        // (docs/adr/0010). Pure Dapper, so a retry has no identity map to go stale.
        //
        // Serializable for the hold cap below, a COUNT-then-INSERT across units
        // that the per-unit lock does not cover: under Read Committed, N
        // concurrent holds from one client on N units all count before any
        // commits (docs/adr/0016; HoldAvailabilityConcurrencyTests).
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        // Outside the retried delegate (docs/adr/0025). A fresh id per attempt
        // would make a retry after a committed-but-unacknowledged insert collide
        // with its own hold on the exclusion constraint: the caller is told the
        // unit is unavailable while that hold stays live, unreachable, and
        // counted against their cap. With one id, the catch below can ask
        // whether the conflicting row is this request's own.
        Guid holdId = Guid.CreateVersion7();

        (Guid HoldId, DateTimeOffset HoldExpiresAt) result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            DateTimeOffset now = timeProvider.GetUtcNow();

            // Exclusive, so concurrent holds on one unit queue for one short
            // transaction. Arbitrated by the exclusion constraint alone,
            // concurrent inserters wait on each other's uncommitted index entries
            // until deadlock_timeout breaks the cycle, and the retries back off -
            // ten requests for one unit take over a minute. Serialised, the
            // constraint sees committed rows and answers at once (docs/adr/0010).
            //
            // It also excludes archival, which takes the same lock: otherwise
            // DeleteUnitHandler can check "no active holds", this handler can
            // insert one, and the unit is archived with live inventory against
            // it (docs/adr/0028).
            await connection.ExecuteAsync(new CommandDefinition(
                UnitAvailabilityLock.AcquireForHoldSql,
                new { LockKey = UnitAvailabilityLock.KeyFor(request.UnitId) },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            // Re-read under the lock. The pricing lookup above ran before this transaction, so archival
            // can take the lock, archive and commit in between; the lock only orders the two, and this
            // read is what tells this handler that archival won. A locking read, because this
            // transaction's Serializable snapshot predates the wait for the lock and would still show
            // the unit live (docs/adr/0028).
            if (!await unitLookup.IsUnitLiveForWriteAsync(request.UnitId, cancellationToken))
            {
                throw new NotFoundException("Unit", request.UnitId);
            }

            // Stale holds from abandoned checkouts otherwise sit in 'held'
            // forever, permanently occupying their slot in the exclusion
            // constraint below even though GetPriceCalendarHandler already
            // treats them as available. Scoped to this unit and run right
            // before the INSERT they'd actually block.
            const string cleanupSql = $"""
                                      DELETE FROM {BookingsModel.Schema}.unit_availability_holds
                                      WHERE unit_id = @UnitId AND status = 'held' AND hold_expires_at <= @Now;
                                      """;

            await connection.ExecuteAsync(new CommandDefinition(
                cleanupSql,
                new { request.UnitId, Now = now },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            // Counts this client network's live holds across every unit -
            // both the ones still being chosen ('held') and the ones already
            // taken into a checkout ('pending_payment'). Counting only 'held'
            // would let a caller escape the cap by submitting checkout without
            // paying, while the exclusion constraint went on blocking the range.
            //
            // Counting 'pending_payment' does not deny checkout to others behind
            // one NAT: the cap gates taking a new hold, never paying for one. And
            // those rows are finite - Booking.PaymentDueAt bounds them and
            // ExpireUnpaidBookingsJob enforces it.
            //
            // 'booked' is excluded: nothing clears it, so counting it would cost a
            // customer hold capacity permanently after their Nth stay.
            //
            // hold_expires_at > @Now applies to 'held' rows only.
            // 'pending_payment' rows are past that clock by construction -
            // PaymentDueAt governs them - so testing it here would exclude every
            // one of them and reopen the escape.
            const string activeHoldCountSql = $"""
                                               SELECT count(*) FROM {BookingsModel.Schema}.unit_availability_holds
                                               WHERE client_key = @ClientKey
                                                 AND (
                                                     (status = '{HoldStatuses.Held}' AND hold_expires_at > @Now)
                                                     OR status = '{HoldStatuses.PendingPayment}'
                                                 );
                                               """;

            // Before the cap, and that ordering is the whole of this check.
            //
            // Pre-generating holdId let a retry recognise its own committed
            // insert, but only from the catch below - which the cap never lets
            // it reach. A retry counts the hold its own previous attempt
            // committed, so a client at the limit is refused with 429 while the
            // row it cannot see blocks that range and occupies one of its slots
            // for the full hold lifetime. With a cap of one, the first lost
            // acknowledgement locks the client out until the hold expires.
            //
            // One indexed read on a primary key, on a path that is otherwise
            // the hottest write in the system - and it answers a question no
            // other check can: is this row mine?
            //
            // After the stale-hold cleanup above, deliberately. A row that
            // cleanup would have deleted is expired, and handing an expired
            // hold back as a live one would be worse than re-inserting.
            (DateTimeOffset HoldExpiresAt, string Status)? own =
                await FindOwnHoldAsync(connection, holdId, transaction.GetDbTransaction(), cancellationToken);

            if (own is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (holdId, own.Value.HoldExpiresAt);
            }

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

            DateTimeOffset holdExpiresAt = now.Add(HoldDuration);

            const string sql = $"""
                               INSERT INTO {BookingsModel.Schema}.unit_availability_holds (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, subtotal, currency, length_of_stay_discount_amount, client_key)
                               VALUES (@Id, @UnitId, @StayRange, 'held', @HoldExpiresAt, @CreatedAt, @GuestCount, @TotalPrice, @Subtotal, @Currency, @LengthOfStayDiscountAmount, @ClientKey);
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
                        Subtotal = pricing.Subtotal.Amount,
                        // The enum, not .ToString() - CurrencyTypeHandler
                        // writes the character(3) code, the same handler that
                        // reads it back in HoldConfirmation.
                        pricing.TotalPrice.Currency,
                        LengthOfStayDiscountAmount = pricing.LengthOfStayDiscountAmount?.Amount,
                        request.ClientKey
                    },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }
            catch (PostgresException ex) when (ex.IsViolationOf(UnitAvailabilityHoldConfiguration.OverlapExclusionConstraint)
                                                   || ex.IsPrimaryKeyViolationOf(dbContext.UnitAvailabilityHolds))
            {
                // The exclusion constraint means the range is already held or
                // booked for this unit - the double-booking guarantee, a real
                // conflict (HoldAvailabilityConcurrencyTests).
                //
                // But a retry after a committed-but-unacknowledged attempt
                // re-inserts its own row, violating the primary key and
                // overlapping itself on the exclusion constraint; Postgres does
                // not promise which it reports. So the id decides: a hold under
                // this request's own id is its own committed work.
                await transaction.RollbackAsync(cancellationToken);

                // The same lookup as before the cap check, now outside the
                // transaction. A concurrent attempt of this same request can
                // commit in between, so the row can appear after that earlier
                // check found nothing.
                (DateTimeOffset HoldExpiresAt, string Status)? committed =
                    await FindOwnHoldAsync(connection, holdId, transaction: null, cancellationToken);

                if (committed is not null)
                {
                    // Our own, committed. Reporting "unavailable" here would
                    // leave a live hold nobody can reach, counted against this
                    // client's cap for its whole lifetime.
                    return (holdId, committed.Value.HoldExpiresAt);
                }

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

    /// <summary>
    ///     This operation's own hold, by the id it pre-generated.
    ///     <para>
    ///         Ids are version-7 GUIDs minted per invocation, so a row under
    ///         this one can only have been written by an earlier attempt of this
    ///         same request - which makes a hit an unambiguous "my work
    ///         committed" rather than anything about another caller.
    ///     </para>
    ///     <para>
    ///         Status is returned but not filtered on. A row that has since
    ///         moved to 'pending_payment' or 'booked' was consumed by this
    ///         guest's own checkout, and "here is the hold you created" stays
    ///         the truthful answer; refusing it would report failure for two
    ///         operations that both succeeded.
    ///     </para>
    /// </summary>
    private static Task<(DateTimeOffset HoldExpiresAt, string Status)?> FindOwnHoldAsync(
        IDbConnection connection, Guid holdId, IDbTransaction? transaction, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<(DateTimeOffset, string)?>(new CommandDefinition(
            $"""SELECT hold_expires_at, status FROM {BookingsModel.Schema}.unit_availability_holds WHERE id = @Id""",
            new { Id = holdId }, transaction, cancellationToken: cancellationToken));
}
