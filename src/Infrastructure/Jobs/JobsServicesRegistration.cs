using BuildingBlocks.Configuration;
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
///     Wires up TickerQ as the background job scheduler (docs/adr/0002, and docs/adr/0001 for the AOT
///     constraint behind it). The jobs themselves live in their owning module - Bookings'
///     ExpiredHoldsSweepJob, Identity's ExpiredRefreshTokensSweepJob; this project owns only the
///     scheduler registration, its store and the dashboard.
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
            // A host that should not schedule says so, and keeps the operational store and the
            // dashboard: a second web replica has no reason to run the crons a first one already
            // runs (docs/scale-out-findings.md), and a test host has a stronger one - a job's own
            // commit is indistinguishable from the request under test to an injected fault
            // (docs/adr/0025). Read as a string rather than GetValue<bool>, whose binder is
            // reflective (docs/adr/0001).
            if (string.Equals(
                    configuration[$"{AppConfiguration.RootSection}:Jobs:RunScheduler"],
                    "false",
                    StringComparison.OrdinalIgnoreCase))
            {
                options.DisableBackgroundServices();
            }

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
