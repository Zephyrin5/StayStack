using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
using Promotions.Contracts;
namespace Promotions;

public static class PromotionsServicesRegistration
{
    public static IServiceCollection ConfigurePromotionsServices(
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
        services.AddDbContext<AppPromotionsDbContext>((serviceProvider, options) =>
        {
            string connectionString = configuration.GetConnectionString("AppConnection")
                                      ?? throw new InvalidOperationException(
                                          "Connection string for AppPromotionsDbContext not found.");

            options.ConfigureStayStackDefaults(
                connectionString,
                "promotions",
                environment is not null && environment.IsDevelopment());

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
        });

        services.AddScoped<IPromotionRedemption, PromotionRedemption>();

        return services;
    }
}
