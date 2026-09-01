using Availability.Contracts;
using Availability.Features.HoldAvailability;
using Catalog.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace Availability;

public static class AvailabilityServicesRegistration
{
    public static IServiceCollection ConfigureAvailabilityServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {
        // Registered as a service so its own dependencies
        // (ICurrentUserProvider, TimeProvider) resolve through DI rather
        // than being newed up by hand.
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        // Registered unconditionally, including under "Testing" - see the
        // note in IdentityServicesRegistration on why production
        // registration code shouldn't decide to skip itself under tests.
        // IntegrationTestWebApplicationFactory overrides this DbContext's
        // connection via RemoveAll + a fresh AddDbContext call.
        services.AddDbContext<AppAvailabilityDbContext>((serviceProvider, options) =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException(
                                          "Connection string for AppAvailabilityDbContext not found.");

            options.ConfigureStayStackDefaults(
                connectionString,
                "availability",
                environment is not null && environment.IsDevelopment());

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        // Same "RateLimiting" section Api.RateLimiting's options bind to -
        // MaxActiveHoldsPerClient is a sibling key alongside
        // HoldPermitLimit/HoldWindowSeconds. Bound here rather than in
        // Program.cs because the handler that reads it lives in this module.
        services.Configure<HoldCapOptions>(configuration.GetSection("RateLimiting"));

        services.AddScoped<IHoldConfirmation, HoldConfirmation>();

        // Implements a Catalog-defined interface, not one of Availability's
        // own - see IUnitAvailabilityLookup's own doc comment for why the
        // interface lives on the Catalog side of this relationship.
        services.AddScoped<IUnitAvailabilityLookup, UnitAvailabilityLookup>();

        return services;
    }
}
