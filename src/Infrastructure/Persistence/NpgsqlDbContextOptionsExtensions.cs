using Microsoft.EntityFrameworkCore;
namespace Persistence;

public static class NpgsqlDbContextOptionsExtensions
{
    /// <summary>
    ///     One place for UseNpgsql, the snake_case convention, the retry policy and sensitive logging.
    ///     <paramref name="moduleName"/> names the migrations history table, keeping the application's
    ///     ("app") and TickerQ's ("jobs") independent in one database.
    ///     <para>
    ///         Every caller passes the same "AppConnection" string unmodified, so Npgsql pools them
    ///         together rather than once per caller - a distinguishing parameter would multiply the
    ///         connection count instead of sharing it. The size is set on the connection string, which
    ///         AddAppDbContext requires outside Development (docs/scale-out-findings.md).
    ///     </para>
    /// </summary>
    public static void ConfigureStayStackDefaults(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string moduleName,
        bool isDevelopment,
        string? migrationsAssembly = null)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            // EF Core resolves migrations from the DbContext's own assembly
            // by default, giving one migration set per module for free -
            // except Jobs (TickerQDbContext), whose DbContext lives in the
            // TickerQ.EntityFrameworkCore package itself, so it has to name
            // its migrations assembly explicitly.
            npgsql.MigrationsHistoryTable($"__ef_migrations_history_{moduleName}");

            // Npgsql does not classify 40P01 (deadlock_detected) or 40001
            // (serialization_failure) as transient. Deadlocks occur under
            // concurrent holds (HoldAvailabilityConcurrencyTests), and the hold
            // transaction is Serializable. Widens retry semantics for every
            // DbContext, accepted because both are retriable by definition. An
            // explicit transaction is retried only inside
            // CreateExecutionStrategy().ExecuteAsync.
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: ["40P01", "40001"]);

            if (migrationsAssembly is not null)
            {
                npgsql.MigrationsAssembly(migrationsAssembly);
            }
        });

        builder.UseSnakeCaseNamingConvention();

        if (isDevelopment)
        {
            builder.EnableSensitiveDataLogging();
        }
    }
}
