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
///     Wires up TickerQ as this app's background job scheduler - chosen over
///     Hangfire specifically because its source-generated [TickerFunction]
///     dispatch (see Catalog's ExpiredHoldsSweepJob, Identity's
///     ExpiredRefreshTokensSweepJob) has no runtime reflection in the job
///     invocation path, unlike Hangfire's serialize-a-method-call-then-
///     MethodInfo.Invoke-it model - the one option that didn't directly
///     conflict with IsAotCompatible already being true across this whole
///     solution (see Directory.Build.props and ci.yml's advisory
///     PublishAot/PublishTrimmed check).
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
