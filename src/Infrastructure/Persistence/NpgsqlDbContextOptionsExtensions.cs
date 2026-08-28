using Microsoft.EntityFrameworkCore;
namespace Persistence;

public static class NpgsqlDbContextOptionsExtensions
{
    /// <summary>
    ///     Every module's ServicesRegistration calls this instead of
    ///     repeating UseNpgsql/UseSnakeCaseNamingConvention/sensitive
    ///     logging by hand. moduleName becomes part of the migrations
    ///     history table name so each module's migration history stays
    ///     independent even though they share one physical database - see
    ///     the earlier note on why that matters once two DbContexts target
    ///     the same connection string.
    ///     <para>
    ///         Pool sizing: every module's ServicesRegistration resolves the
    ///         same literal "AppConnection" string and passes it here
    ///         unmodified (no per-module Application Name/search_path). Npgsql
    ///         pools connections by exact connection-string text at the driver
    ///         level, so this is one shared pool (Npgsql's default Maximum
    ///         Pool Size=100) across every DbContext in the app, not one pool
    ///         per module - do not "fix" this by giving each module a
    ///         distinguishing connection-string param, that would multiply the
    ///         total connection count instead of sharing it. Tune pool size,
    ///         if it's ever needed, via "Maximum Pool Size=N" on the
    ///         AppConnection secret itself.
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
            // EF Core resolves migrations from the DbContext's assembly by
            // default. Keeping that default avoids runtime assembly-name
            // lookup while preserving one migration set per module - except
            // for Jobs (TickerQDbContext), whose DbContext type is declared
            // inside the TickerQ.EntityFrameworkCore package itself rather
            // than a project this solution owns, so it has to name its
            // migrations assembly explicitly instead.
            npgsql.MigrationsHistoryTable($"__ef_migrations_history_{moduleName}");

            // Npgsql's own transient-error classification doesn't include
            // 40P01 (deadlock_detected) by default - confirmed against a
            // real deadlock HoldAvailabilityConcurrencyTests reproduced
            // under genuine 10-way concurrent contention on the same
            // range, which this alone doesn't fix (see
            // HoldAvailabilityHandler's own CreateExecutionStrategy wrap
            // for the other half: this only makes the error retriable,
            // something still has to actually invoke the retry loop).
            // 40001 (serialization_failure) added alongside it for the same
            // reason, once CreatePricingRuleHandler started wrapping its
            // check-then-insert in an IsolationLevel.Serializable
            // transaction (see docs/adr/0012) - without it, a genuine
            // concurrent conflict there would surface as an unhandled 500
            // instead of retrying, the opposite of what Serializable was
            // added for. This widens retry semantics for every DbContext in
            // the app, not just that one path - accepted, since a
            // serialization failure is retriable by definition everywhere
            // it can occur.
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
