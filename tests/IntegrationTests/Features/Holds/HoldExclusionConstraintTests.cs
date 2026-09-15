// Proves concurrent inserts aligned by a Barrier are decided by the exclusion constraint, through
// the handler's lock protocol and through none. With holds in shared lock mode the protocol test
// fails on its first race: every loser is a deadlock victim rather than a clean rejection.
using BuildingBlocks.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using System.Diagnostics;
namespace IntegrationTests.Features.Holds;

// No double-booking, driven at the database directly - no HTTP, no EF, no
// handler - so it runs in milliseconds and can go hundreds of iterations where
// HoldAvailabilityConcurrencyTests manages one burst per test.
//
// Each inserter follows exactly the protocol HoldAvailabilityHandler uses: its
// own transaction, UnitAvailabilityLock in the mode the handler takes it
// (UnitAvailabilityLock.AcquireForHoldSql - one definition, so the two cannot
// drift), then the insert. What decides the race is the exclusion constraint.
[Collection("Integration Tests")]
public class HoldExclusionConstraintTests(IntegrationTestWebApplicationFactory factory)
{
    private const int Inserters = 8;

    private enum Outcome
    {
        Inserted,
        RejectedByTheConstraint,
        Deadlocked
    }

    private NpgsqlDataSource DataSource() =>
        NpgsqlDataSource.Create(
            factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("AppConnection")!);

    private static async Task<Outcome> InsertHoldAsync(
        NpgsqlDataSource dataSource, Guid unitId, DateOnly checkIn, string? lockSql, Barrier start)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        // Everyone opens their transaction first, then goes at once.
        start.SignalAndWait(TestContext.Current.CancellationToken);

        try
        {
            if (lockSql is not null)
            {
                await using NpgsqlCommand @lock = new NpgsqlCommand(lockSql, connection, transaction);
                @lock.Parameters.AddWithValue("LockKey", UnitAvailabilityLock.KeyFor(unitId));
                await @lock.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                // Without the lock these inserts deadlock, and each deadlock waits
                // out deadlock_timeout (1s by default) before a victim is chosen.
                // Shortened for this transaction only - it changes how fast a
                // deadlock is found, not what a victim does, which is roll back.
                // Allowed because the test container connects as a superuser.
                await using NpgsqlCommand fast = new NpgsqlCommand("SET LOCAL deadlock_timeout = '20ms'", connection, transaction);
                await fast.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using NpgsqlCommand insert = new NpgsqlCommand("""
                INSERT INTO unit_availability_holds
                    (id, unit_id, stay_range, status, hold_expires_at, created_at, guest_count, total_price, subtotal, currency)
                VALUES (@Id, @UnitId, @StayRange, 'held', now() + interval '15 minutes', now(), 2, 200, 200, 'KWD')
                """, connection, transaction);
            insert.Parameters.AddWithValue("Id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("UnitId", unitId);
            insert.Parameters.AddWithValue("StayRange", new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            return Outcome.Inserted;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return Outcome.RejectedByTheConstraint;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return Outcome.Deadlocked;
        }
    }

    private static async Task<long> HoldsForAsync(NpgsqlDataSource dataSource, Guid unitId)
    {
        await using NpgsqlCommand count = dataSource.CreateCommand(
            "SELECT count(*) FROM unit_availability_holds WHERE unit_id = @UnitId");
        count.Parameters.AddWithValue("UnitId", unitId);
        return (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Outcome[]> RaceAsync(NpgsqlDataSource dataSource, Guid unitId, string? lockSql)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(300);
        Barrier start = new Barrier(Inserters);

        // Each inserter shifts its range by a day, so every pair overlaps.
        return await Task.WhenAll(Enumerable.Range(0, Inserters).Select(i =>
            Task.Run(() => InsertHoldAsync(dataSource, unitId, checkIn.AddDays(i % 2), lockSql, start))));
    }

    [Fact]
    public async Task ConcurrentHoldInserts_FollowingTheHandlersProtocol_AdmitExactlyOne_WithoutDeadlocking()
    {
        const int iterations = 100;
        await using NpgsqlDataSource dataSource = DataSource();

        Stopwatch elapsed = Stopwatch.StartNew();

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Guid unitId = Guid.CreateVersion7();
            Outcome[] outcomes = await RaceAsync(dataSource, unitId, UnitAvailabilityLock.AcquireForHoldSql);

            Assert.Equal(1, outcomes.Count(o => o == Outcome.Inserted));
            Assert.Equal(1, await HoldsForAsync(dataSource, unitId));

            // Every loser rejected cleanly by the constraint, none a deadlock
            // victim - asserted per race, so a regression fails on its first
            // deadlock instead of after a hundred of them.
            Assert.Equal(Inserters - 1, outcomes.Count(o => o == Outcome.RejectedByTheConstraint));
        }

        // Not only correct but prompt. With the lock taken in shared mode,
        // concurrent inserters for one unit each saw the other's uncommitted index
        // entry and waited on it, and Postgres broke each cycle after
        // deadlock_timeout: 10 races here produced 70 deadlocks and took 70
        // seconds, every loser a deadlock victim rather than a clean rejection.
        // Through the handler, whose execution strategy retries with backoff, one
        // burst of ten hold requests took over a minute.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"{iterations} races took {elapsed.Elapsed} - something is waiting rather than arbitrating.");
    }

    [Fact]
    public async Task ConcurrentHoldInserts_WithNoLockAtAll_StillNeverAdmitTwo()
    {
        // The constraint on its own, for a writer that never takes the lock - a
        // data migration, an admin script, a future handler. It deadlocks under
        // this load, which is why the handler serialises, but a deadlock victim
        // rolls back: two overlapping holds for one unit must never both commit.
        const int iterations = 50;
        await using NpgsqlDataSource dataSource = DataSource();

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Guid unitId = Guid.CreateVersion7();
            await RaceAsync(dataSource, unitId, lockSql: null);

            Assert.True(await HoldsForAsync(dataSource, unitId) <= 1);
        }
    }
}
