using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using System.Text;
namespace IntegrationTests.Measurements;

// Investigation for Track B.1, not a suite test: does one cron occurrence execute once when two
// instances share a database? Skipped unless STAYSTACK_TWO_HOST names an output file, because it has
// to wait for a real minute boundary to pass.
//
// It starts a second host against the same database, waits, and reads TickerQ's own occurrence rows -
// which is the only place the answer is visible. This application's jobs are idempotent either way,
// so their side effects cannot distinguish one execution from two.
[Collection(MeasurementsCollection.Name)]
public class TwoHostSchedulerProbe(MeasurementsFixture factory)
{
    [Fact]
    public async Task OneOccurrencePerCronTick()
    {
        string? target = Environment.GetEnvironmentVariable("STAYSTACK_TWO_HOST");
        Assert.SkipUnless(target is { Length: > 0 }, "Investigation only: set STAYSTACK_TWO_HOST to an output file.");

        CancellationToken ct = TestContext.Current.CancellationToken;

        // Both hosts run TickerQ against one database, which is the shape a second replica has.
        await using WebApplicationFactory<Program> second = factory.WithWebHostBuilder(_ => { });
        second.CreateClient();
        factory.CreateClient();

        DateTimeOffset started = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(150), ct);

        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(ct);

        var occurrences = (await connection.QueryAsync(
            """
            SELECT c.function AS function,
                   o.execution_time AS execution_time,
                   o.status AS status,
                   o.lock_holder AS lock_holder,
                   count(*) OVER (PARTITION BY o.cron_ticker_id, o.execution_time) AS rows_for_that_tick
            FROM ticker."CronTickerOccurrences" o
            JOIN ticker."CronTickers" c ON c.id = o.cron_ticker_id
            WHERE o.execution_time >= @Started
            ORDER BY o.execution_time, c.function
            """,
            new { Started = started.UtcDateTime })).ToList();

        StringBuilder report = new StringBuilder();
        report.AppendLine($"two hosts sharing one database, {started:O} + 150s");
        report.AppendLine($"occurrence rows: {occurrences.Count}");
        report.AppendLine();

        foreach (var row in occurrences)
        {
            report.AppendLine(
                $"{row.function} at {row.execution_time:HH:mm:ss} status={row.status} " +
                $"lock_holder={row.lock_holder} rows_for_that_tick={row.rows_for_that_tick}");
        }

        await File.WriteAllTextAsync(target!, report.ToString(), ct);
    }
}
