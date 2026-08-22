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
        // Registered unconditionally, including under "Testing" - see the
        // note in IdentityServicesRegistration on why production
        // registration code shouldn't be the one deciding to skip itself
        // under tests. IntegrationTestWebApplicationFactory overrides this
        // DbContext's connection via RemoveAll + a fresh AddDbContext call.

        // Registered as a service so its own dependencies
        // (ICurrentUserProvider, TimeProvider) resolve through DI rather
        // than being newed up by hand.
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<AppCatalogDbContext>((serviceProvider, options) =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException(
                                          "Connection string for AppCatalogDbContext not found.");

            options.ConfigureStayStackDefaults<AppCatalogDbContext>(
                connectionString,
                "catalog",
                environment is not null && environment.IsDevelopment());

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<IUnitLookup, UnitLookup>();
        services.AddScoped<IHoldConfirmation, HoldConfirmation>();

        return services;
    }
}
