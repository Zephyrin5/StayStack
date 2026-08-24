using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence;
using TickerQ.Dashboard.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.DbContextFactory;
using TickerQ.EntityFrameworkCore.DependencyInjection;
namespace Jobs;

/// <summary>
///     Wires up TickerQ as this app's background job scheduler - see
///     docs/adr/0002 for why TickerQ over Hangfire/Quartz, and
///     docs/adr/0001 for the Native AOT constraint that decision follows
///     from. Jobs themselves (Catalog's ExpiredHoldsSweepJob, Identity's
///     ExpiredRefreshTokensSweepJob) live in their owning module, not here -
///     this project only owns scheduler registration, the operational
///     store, and the dashboard.
/// </summary>
public static class JobsServicesRegistration
{
    public static IServiceCollection ConfigureJobsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment? environment = null)
    {
        services.AddTickerQ(options =>
        {
            options.AddOperationalStore(efOptions =>
            {
                efOptions.UseTickerQDbContext<TickerQDbContext>(dbOptions =>
                {
                    string connectionString = configuration.GetConnectionString("AppConnection")
                                              ?? throw new InvalidOperationException(
                                                  "Connection string for TickerQDbContext not found.");

                    dbOptions.ConfigureStayStackDefaults(
                        connectionString,
                        "jobs",
                        environment is not null && environment.IsDevelopment(),
                        migrationsAssembly: "Jobs");
                });
            });

            // Basic-auth/API-key are TickerQ's own separate credential
            // stores - WithHostAuthentication delegates to this app's
            // existing JWT bearer auth instead, so the dashboard is gated by
            // the same Administrator role every other admin-only surface
            // uses, not a second set of credentials to manage.
            options.AddDashboard(dashboard =>
            {
                dashboard.SetBasePath("admin/jobs/dashboard");
                dashboard.WithHostAuthentication(AuthorizationPolicies.Administrator);
            });
        });

        return services;
    }
}
