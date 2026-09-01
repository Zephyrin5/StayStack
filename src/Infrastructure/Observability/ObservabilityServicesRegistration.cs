using BuildingBlocks.Observability;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Observability.Configurations;
using Outbox;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text;
namespace Observability;

public static class ObservabilityServicesRegistration
{
    public static IServiceCollection ConfigureObservabilityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ObservabilityConfiguration observability = configuration
                                                       .GetSection("Observability")
                                                       .Get<ObservabilityConfiguration>()
                                                   ?? throw new InvalidOperationException("The 'Observability' configuration section is missing.");

        if (string.IsNullOrWhiteSpace(observability.OtlpEndpoint))
            throw new InvalidOperationException("Observability:OtlpEndpoint is not configured.");

        if (string.IsNullOrWhiteSpace(observability.GrafanaInstanceId))
            throw new InvalidOperationException("Observability:GrafanaInstanceId is not configured.");

        if (string.IsNullOrWhiteSpace(observability.GrafanaAccessPolicyToken))
            throw new InvalidOperationException("Observability:GrafanaAccessPolicyToken is not configured.");

        services.Configure<ObservabilityConfiguration>(configuration.GetSection("Observability"));

        if (observability.CommandTracingEnabled)
        {
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(TelemetryPipelineBehavior<,>));
        }

        string basicAuthHeader = "Authorization=Basic " +
                                 Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                     $"{observability.GrafanaInstanceId}:{observability.GrafanaAccessPolicyToken}"));

        string baseEndpoint = observability.OtlpEndpoint.TrimEnd('/');
        string tracesEndpoint = $"{baseEndpoint}/v1/traces";
        string metricsEndpoint = $"{baseEndpoint}/v1/metrics";
        string logsEndpoint = $"{baseEndpoint}/v1/logs";

        // Filters EF Core's command-level logs out of what gets exported -
        // EnableSensitiveDataLogging means this category logs full SQL
        // parameter values (usernames today, worse later), fine in a dev
        // console but not something that should ship to shared Grafana.
        //
        // Scoped to the OpenTelemetry provider, which is what the sentence
        // above always claimed but the code did not do: the provider-less
        // AddFilter overload applies to *every* provider, so this silenced
        // the dev console too, and would override whatever
        // Logging:LogLevel says for that category. Those levels are set in
        // configuration now - Warning in appsettings.json so production
        // isn't logging a line per SQL statement, Information in
        // appsettings.Development.json so a developer still sees queries -
        // and a global code filter here would quietly win over both.
        //
        // Latent until this registration is uncalled-for-real: Program.cs
        // has it commented out pending Grafana config, which is exactly why
        // the production level had to come from configuration rather than
        // rely on this line.
        services.AddLogging(logging =>
            logging.AddFilter<OpenTelemetryLoggerProvider>(
                "Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None));

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                "staystack-api",
                serviceVersion: typeof(ObservabilityServicesRegistration).Assembly
                    .GetName().Version?.ToString() ?? "unknown"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(CommandTelemetry.SourceName)
                .AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = new Uri(tracesEndpoint);
                    otlp.Headers = basicAuthHeader;
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Wildcard rather than one AddMeter per source. Module-owned
                // meters (Bookings' orphaned-intent counter, and whatever
                // follows it) can't be named here as constants without this
                // Infrastructure project referencing the modules themselves,
                // inverting the dependency direction ADR-0004 sets. Every
                // meter in this codebase is named "StayStack.<area>", so one
                // pattern covers them all and new ones are collected without
                // a change here.
                .AddMeter("StayStack.*")
                .AddConsoleExporter()
                .AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = new Uri(metricsEndpoint);
                    otlp.Headers = basicAuthHeader;
                }))
            .WithLogging(logging => logging
                    .AddOtlpExporter(otlp =>
                    {
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                        otlp.Endpoint = new Uri(logsEndpoint);
                        otlp.Headers = basicAuthHeader;
                    }),
                options =>
                {
                    // Include the formatted message text (not just the
                    // template + args separately) and any active
                    // ILogger scopes - both make the exported log line
                    // readable in Grafana Loki without reconstructing it.
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                });

        return services;
    }
}
