using Microsoft.EntityFrameworkCore;
namespace Persistence;

public static class NpgsqlDbContextOptionsExtensions
{
    /// <summary>
    ///     Every module's ServicesRegistration calls this instead of
    ///     repeating UseNpgsql/UseSnakeCaseNamingConvention/sensitive logging
    ///     by hand. moduleName becomes part of the migrations history table
    ///     name so each module's history stays independent despite sharing
    ///     one physical database.
    ///     <para>
    ///         Pool sizing: every module resolves the same literal
    ///         "AppConnection" string unmodified, so Npgsql pools all of them
    ///         together (default Maximum Pool Size=100) as one shared pool,
    ///         not one per module. Don't "fix" this with a per-module
    ///         distinguishing connection-string param - that multiplies the
    ///         connection count instead of sharing it. Tune pool size via
    ///         "Maximum Pool Size=N" on the AppConnection secret itself.
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

            // Npgsql doesn't classify 40P01 (deadlock_detected) as transient
            // by default - confirmed against a real deadlock
            // HoldAvailabilityConcurrencyTests reproduced under 10-way
            // concurrent contention (this only makes it retriable;
            // HoldAvailabilityHandler's own CreateExecutionStrategy wrap
            // actually invokes the retry). 40001 (serialization_failure)
            // added alongside it once CreatePricingRuleHandler started using
            // IsolationLevel.Serializable (docs/adr/0012) - without it, a
            // genuine conflict there would surface as an unhandled 500
            // instead of retrying. Widens retry semantics for every
            // DbContext, accepted since a serialization failure is
            // retriable by definition everywhere it occurs.
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
