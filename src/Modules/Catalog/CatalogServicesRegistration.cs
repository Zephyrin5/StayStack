using BuildingBlocks.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace Catalog;

public static class CatalogServicesRegistration
{
    public static IServiceCollection ConfigureCatalogServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {

        services.AddSingleton<IModuleModel, CatalogModel>();
        services.AddScoped<CatalogDb>();

        services.AddScoped<IUnitLookup, UnitLookup>();

        return services;
    }
}
