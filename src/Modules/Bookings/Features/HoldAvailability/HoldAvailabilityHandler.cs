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
    AppBookingsDbContext dbContext,
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
    // each blocking up to StaySearchPolicyOptions.MaxStayNights of a unit
    // via the exclusion constraint. Rate says how fast you reach
    // saturation, not how much you can hold. See docs/adr/0016.
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
        // ValidationException, not Guard.Against.*, at all three sites below.
        // These reject caller input, so they need a 400 carrying a message
        // written for the caller. A guard clause throws
        // ArgumentException/ArgumentOutOfRangeException, whose Message the BCL
        // decorates with "(Parameter 'GuestCount')" - and the only way that
        // ever became a 400 was GlobalExceptionHandler mapping the whole
        // ArgumentException family to one, which meant any library's internal
        // ArgumentException was a 400 leaking its message too. The handler that
        // knows a value came from the caller is the right place to say so.
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

        // Outside the retried delegate, deliberately. Generated inside, a retry
        // after a committed-but-unacknowledged attempt produced a *different*
        // id, so the insert collided with its own predecessor on the exclusion
        // constraint and the caller was told the unit was unavailable - while
        // attempt one's hold sat live and unreachable, carrying the same
        // client_key and so burning one of that guest's concurrent-hold slots
        // for its whole lifetime. On a flaky connection, repeatedly.
        //
        // Pre-generating it turns that collision into a question with an
        // answer: is this row mine? See the catch below, and docs/adr/0025.
        // Outside the retried delegate, deliberately. Generated inside, a retry
        // after a committed-but-unacknowledged attempt produced a *different*
        // id, so the insert collided with its own predecessor on the exclusion
        // constraint and the caller was told the unit was unavailable - while
        // attempt one's hold sat live and unreachable, carrying the same
        // client_key and so burning one of that guest's concurrent-hold slots
        // for its whole lifetime. On a flaky connection, repeatedly.
        //
        // Pre-generating it turns that collision into a question with an
        // answer: is this row mine? See the catch below, and docs/adr/0025.
        Guid holdId = Guid.CreateVersion7();

        (Guid HoldId, DateTimeOffset HoldExpiresAt) result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            IDbConnection connection = dbContext.Database.GetDbConnection();

            DateTimeOffset now = timeProvider.GetUtcNow();

            // Shared, so concurrent holds on one unit still run in parallel -
            // they arbitrate through the exclusion constraint below, and
            // queueing them would slow the hottest write in the system to
            // defend against an operation a host performs by hand. What it
            // does block is archival, which takes the same lock exclusively:
            // without it, DeleteUnitHandler can check "no active holds", this
            // handler can insert one, and the unit is archived with live
            // inventory against it. See BuildingBlocks.UnitAvailabilityLock.
            await connection.ExecuteAsync(new CommandDefinition(
                AdvisoryLock.AcquireSharedSql,
                new { LockKey = UnitAvailabilityLock.KeyFor(request.UnitId) },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            // Re-read under the lock, and this is what makes the lock worth
            // anything. The pricing lookup above runs before this transaction
            // opens, so it saw the unit while it still existed; without this,
            // archival can win the lock, archive, and commit, and this handler
            // then inserts a hold against a unit that is gone. Taking the lock
            // only orders the two - it does not tell either of them what the
            // other did.
            if (await unitLookup.GetUnitAsync(request.UnitId, cancellationToken) is null)
            {
                throw new NotFoundException("Unit", request.UnitId);
            }

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

            // Counts this client network's live holds across every unit -
            // both the ones still being chosen ('held') and the ones already
            // taken into a checkout ('pending_payment').
            //
            // Counting only 'held' left the cap trivially escapable, and not
            // by a clever attack: hold a unit, POST the checkout form without
            // paying, and the row moves to 'pending_payment' where the cap
            // stopped seeing it - while the exclusion constraint went on
            // blocking its range just the same. Repeat, and one anonymous
            // caller accumulates as many blocked ranges as they have patience
            // for, each one costing them nothing. The per-client cap was the
            // only concurrency bound on that, and confirming was the way out
            // of it.
            //
            // The objection to counting 'pending_payment' was that it denies
            // checkout to everyone behind one NAT. It doesn't - the cap gates
            // *taking a new hold*, never paying for one already taken - and
            // what makes the residual sharing acceptable is that these rows
            // are now finite: Booking.PaymentDueAt bounds them and
            // ExpireUnpaidBookingsJob enforces it, so a slot occupied by
            // somebody's abandoned checkout returns within the payment window
            // instead of never. That was not true when the objection was
            // raised, and it is what changed the answer.
            //
            // 'booked' stays excluded. ConfirmHoldAsync's payment transition
            // sets it and nothing ever clears it, so counting it would mean a
            // customer permanently loses hold capacity after their Nth
            // successful stay - a customer-facing bug, not an index-tuning
            // choice.
            //
            // hold_expires_at > @Now applies to 'held' rows only.
            // 'pending_payment' rows are past that clock by construction: the
            // transition stops hold_expires_at governing them and
            // PaymentDueAt takes over, so testing it here would exclude every
            // one of them and restore the escape this closes.
            const string activeHoldCountSql = $"""
                                               SELECT count(*) FROM unit_availability_holds
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

            const string sql = """
                               INSERT INTO unit_availability_holds (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, subtotal, currency, length_of_stay_discount_amount, client_key)
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
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ExclusionViolation
                                                   or PostgresErrorCodes.UniqueViolation)
            {
                // Two very different things arrive here, and telling them apart
                // is the whole of it.
                //
                // The exclusion constraint means some or all of the requested
                // range is already held or booked for this unit. That IS the
                // double-booking guarantee - no rows-affected check, no manual
                // locking, the constraint does the work - and it is a real
                // conflict rather than a transient one, so it propagates
                // straight out of ExecuteAsync. See
                // HoldAvailabilityConcurrencyTests.
                //
                // But this insert now carries a pre-generated id, so a retry
                // after a committed-but-unacknowledged attempt re-inserts a row
                // that is already there - and that violates the primary key
                // *and* overlaps itself on the exclusion constraint. Postgres
                // does not promise which of the two it reports, so branching on
                // the SqlState would be a coin flip. The id answers directly:
                // if a hold under our own pre-generated id exists, this attempt
                // is looking at its own committed work.
                await transaction.RollbackAsync(cancellationToken);

                // The same lookup as before the cap check, now that the
                // transaction is gone. Still needed here as well as there: a
                // concurrent attempt of this same request can commit in
                // between, so the row can appear after that earlier check found
                // nothing.
                (DateTimeOffset HoldExpiresAt, string Status)? committed =
                    await FindOwnHoldAsync(connection, holdId, transaction: null, cancellationToken);

                if (committed is not null)
                {
                    // Our own, committed. Returning it is what stops a flaky
                    // connection stranding inventory: the alternative left a
                    // live hold nobody could reach, still counting against this
                    // client's cap for its whole lifetime, while the caller was
                    // told the unit was unavailable.
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
            """SELECT hold_expires_at, status FROM unit_availability_holds WHERE id = @Id""",
            new { Id = holdId }, transaction, cancellationToken: cancellationToken));
}
