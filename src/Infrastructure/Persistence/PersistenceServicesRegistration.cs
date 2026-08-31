using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Persistence.DapperTypeHandlers;
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
        return services;
    }
}
