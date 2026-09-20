using Npgsql;
using BuildingBlocks.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Persistence.DapperTypeHandlers;
using Persistence.Interceptors;
namespace Persistence;

public static class PersistenceServicesRegistration
{
    // Process-global Dapper configuration, not Catalog-specific despite
    // living alongside Catalog's own DapperTypeHandlers folder -
    // SqlMapper.AddTypeHandler has no per-module scoping, so this applies
    // to every Dapper query in the app regardless of which module calls it.
    public static IServiceCollection ConfigurePersistenceServices(
        this IServiceCollection services)
    {
        SqlMapper.AddTypeHandler(new NpgsqlRangeTypeHandler<DateOnly>());
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new CurrencyTypeHandler());
        return services;
    }

    /// <summary>
    ///     The one <see cref="AppDbContext"/>. Its model comes from the <see cref="IModuleModel"/>
    ///     registrations each module adds, so this knows no module.
    /// </summary>
    public static IServiceCollection AddAppDbContext(
        this IServiceCollection services, string connectionString, bool isDevelopment)
    {
        RequireAnExplicitPoolSize(connectionString, isDevelopment);

        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options.ConfigureStayStackDefaults(connectionString, "app", isDevelopment, migrationsAssembly: "Database");
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<ITransactionRunner, TransactionRunner>();

        return services;
    }

    /// <summary>
    ///     Refuses to start without a stated pool size, outside Development.
    ///     <para>
    ///         Npgsql pools per connection string and defaults to 100 connections per process, so N
    ///         instances ceiling at N x 100 against one Postgres - past a typical max_connections of
    ///         100-200 with two instances, and it fails as refusals under load rather than gradually.
    ///         The number depends on how many instances run and what the server allows, which only the
    ///         deployment knows: instances x pool <= max_connections, less headroom for migrations,
    ///         psql and the jobs dashboard (docs/scale-out-findings.md).
    ///     </para>
    /// </summary>
    private static void RequireAnExplicitPoolSize(string connectionString, bool isDevelopment)
    {
        // ShouldSerialize, not ContainsKey: the builder answers ContainsKey for every keyword it knows,
        // set or not, so it cannot tell a stated 100 from the default one.
        if (isDevelopment || new NpgsqlConnectionStringBuilder(connectionString).ShouldSerialize("Maximum Pool Size"))
        {
            return;
        }

        throw new InvalidOperationException(
            "The AppConnection connection string does not set Maximum Pool Size. Npgsql then pools 100 " +
            "connections per instance, so two instances already exceed a typical max_connections and the " +
            "failure arrives as connection refusals under load. Set it deliberately: instances x pool must " +
            "fit inside max_connections with headroom for migrations, psql and the jobs dashboard " +
            "(docs/scale-out-findings.md).");
    }
}
