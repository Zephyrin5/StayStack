using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
namespace Persistence;

/// <summary>
///     Answers whether this node can actually reach the database, which is
///     the only thing that separates a node able to serve requests from one
///     able to return 500s quickly.
///     <para>
///         Registered against readiness, never liveness. The distinction is
///         not bookkeeping: an orchestrator restarts a container that fails
///         liveness and merely stops routing to one that fails readiness. A
///         database outage makes every node unready at once, and if that also
///         read as "not alive" the whole deployment would enter a restart
///         loop - adding a thundering herd of reconnecting nodes to an
///         already-struggling database, and losing the in-flight work that
///         would otherwise have survived the blip.
///     </para>
/// </summary>
public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <summary>
    ///     Bounded well under a typical probe timeout so an unreachable
    ///     database fails the check rather than hanging it. Npgsql's own
    ///     connect timeout defaults to 15 seconds, which outlasts most
    ///     orchestrator probe deadlines - the probe would be recorded as a
    ///     timeout instead of a failure, and each poll would leave another
    ///     connection attempt in flight behind it.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? connectionString = configuration.GetConnectionString("AppConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("No database connection string is configured.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            // The same literal connection string every DbContext resolves, so
            // this draws from the one shared Npgsql pool rather than opening
            // anything of its own - see NpgsqlDbContextOptionsExtensions. A
            // poll costs a pooled round trip, not a connection.
            await using NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);

            // Deliberately not a query against any table. Readiness is asking
            // "is the database reachable and answering", and a real query
            // would also fail on an unmigrated or empty schema - which is a
            // different problem with a different remedy, and not one that
            // should take a node out of rotation.
            await using NpgsqlCommand command = new NpgsqlCommand("SELECT 1", connection);
            command.CommandTimeout = (int)Timeout.TotalSeconds;
            await command.ExecuteScalarAsync(timeout.Token);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            // The description carries no exception detail, and the exception
            // is deliberately not attached. Health endpoints are anonymous by
            // design, and an Npgsql failure message names the host, port and
            // user it tried. The default response writer emits only the
            // status word, so nothing here reaches the wire today - this is
            // about what a future custom writer would find sitting in the
            // result if it went looking.
            return HealthCheckResult.Unhealthy("The database is not reachable.");
        }
    }
}
