using Npgsql;
using System.Text;
namespace IntegrationTests.Measurements;

/// <summary>
///     Polls pg_stat_activity for sessions idle inside a transaction and
///     records the longest observation per statement, for the Stage 0
///     measurement. Started by IntegrationTestWebApplicationFactory only when
///     STAYSTACK_IDLE_TX_PROBE names an output file.
///     <para>
///         The query shown is the last statement the session ran before going
///         idle, which identifies the operation holding the transaction open.
///         Polling granularity is the interval below, so durations are lower
///         bounds.
///     </para>
/// </summary>
public sealed class IdleInTransactionProbe(string connectionString, string outputPath) : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (double MaxMs, int Samples)> _byQuery = new();
    private Task? _loop;
    private int _polls;

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
                           SELECT query, EXTRACT(EPOCH FROM (clock_timestamp() - state_change)) * 1000
                           FROM pg_stat_activity
                           WHERE state = 'idle in transaction' AND datname = current_database() AND pid <> pg_backend_pid()
                           """;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    string query = Normalise(reader.GetString(0));
                    double ms = Convert.ToDouble(reader.GetValue(1));
                    lock (_byQuery)
                    {
                        (double max, int samples) = _byQuery.GetValueOrDefault(query);
                        _byQuery[query] = (Math.Max(max, ms), samples + 1);
                    }
                }

                _polls++;
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (NpgsqlException)
            {
                // The container stopping under the probe at the end of a run.
                break;
            }
        }
    }

    private static string Normalise(string query)
    {
        string single = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length > 300 ? single[..300] : single;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        StringBuilder report = new StringBuilder();
        report.AppendLine($"polls={_polls} interval_ms={Interval.TotalMilliseconds}");
        foreach ((string query, (double maxMs, int samples)) in _byQuery.OrderByDescending(kv => kv.Value.MaxMs))
        {
            report.AppendLine($"{maxMs,10:F0} ms  samples={samples,5}  {query}");
        }

        await File.WriteAllTextAsync(outputPath, report.ToString());
        _stop.Dispose();
    }
}
