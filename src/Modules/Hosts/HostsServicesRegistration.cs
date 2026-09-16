using BuildingBlocks.Persistence;
using Hosts.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace Hosts;

public static class HostsServicesRegistration
{
    public static IServiceCollection ConfigureHostsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {

        services.AddSingleton<IModuleModel, HostsModel>();
        services.AddScoped<HostsDb>();

        services.AddScoped<IHostLookup, HostLookup>();
        services.AddScoped<IHostRegistrar, HostRegistrar>();
        services.AddScoped<IHostAuthorization, HostAuthorization>();

        return services;
    }
}
