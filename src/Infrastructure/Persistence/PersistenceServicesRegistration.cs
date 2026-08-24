using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Persistence.DapperTypeHandlers;
namespace Persistence;

public static class PersistenceServicesRegistration
{
    // Process-global Dapper configuration, not Catalog-specific despite
    // living alongside Catalog's own DapperTypeHandlers folder - Dapper's
    // SqlMapper.AddTypeHandler registry has no per-DbContext/module scoping
    // of its own, so these apply to every Dapper query anywhere in the app
    // regardless of which module's ServicesRegistration ends up calling
    // this. Registered once, here, rather than duplicated per module.
    public static IServiceCollection ConfigurePersistenceServices(
        this IServiceCollection services)
    {
        SqlMapper.AddTypeHandler(new NpgsqlRangeTypeHandler<DateOnly>());
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        return services;
    }
}
