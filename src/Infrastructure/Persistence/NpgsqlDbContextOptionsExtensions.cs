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
            npgsql.EnableRetryOnFailure();

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
