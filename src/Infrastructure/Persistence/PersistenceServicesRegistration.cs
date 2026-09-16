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
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options.ConfigureStayStackDefaults(connectionString, "app", isDevelopment, migrationsAssembly: "Database");
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<ITransactionRunner, TransactionRunner>();

        return services;
    }
}
