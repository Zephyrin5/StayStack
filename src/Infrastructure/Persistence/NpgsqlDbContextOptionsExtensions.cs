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
        bool isDevelopment)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            // EF Core resolves migrations from the DbContext's assembly by
            // default. Keeping that default avoids runtime assembly-name
            // lookup while preserving one migration set per module.
            npgsql.MigrationsHistoryTable($"__ef_migrations_history_{moduleName}");
            npgsql.EnableRetryOnFailure();
        });

        builder.UseSnakeCaseNamingConvention();

        if (isDevelopment)
        {
            builder.EnableSensitiveDataLogging();
        }
    }
}
