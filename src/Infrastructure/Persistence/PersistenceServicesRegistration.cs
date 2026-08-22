using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Persistence.DapperTypeHandlers;
namespace Persistence;

public static class CatalogServicesRegistration
{
    public static IServiceCollection ConfigurePersistenceServices(
        this IServiceCollection services)
    {
        SqlMapper.AddTypeHandler(new NpgsqlRangeTypeHandler<DateOnly>());
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        return services;
    }
}
