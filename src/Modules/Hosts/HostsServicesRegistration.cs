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
        // Registered unconditionally, including under "Testing" - see the
        // note in IdentityServicesRegistration on why production
        // registration code shouldn't be the one deciding to skip itself
        // under tests. IntegrationTestWebApplicationFactory overrides this
        // DbContext's connection via RemoveAll + a fresh AddDbContext call.

        // Registered as a service so its own dependencies
        // (ICurrentUserProvider, TimeProvider) resolve through DI rather
        // than being newed up by hand.
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<AppHostsDbContext>((serviceProvider, options) =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException(
                                          "Connection string for AppHostsDbContext not found.");

            options.ConfigureStayStackDefaults<AppHostsDbContext>(
                connectionString,
                "hosts",
                environment is not null && environment.IsDevelopment());

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<IHostLookup, HostLookup>();
        services.AddScoped<IHostRegistrar, HostRegistrar>();
        services.AddScoped<IHostAuthorization, HostAuthorization>();

        return services;
    }
}
