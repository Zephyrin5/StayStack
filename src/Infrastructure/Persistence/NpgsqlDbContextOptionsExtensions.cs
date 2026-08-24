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
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: ["40P01"]);

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
