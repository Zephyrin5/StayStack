using BuildingBlocks.Configuration;
using BuildingBlocks.Observability;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Observability.Configurations;
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
                                                       .AppSection(ObservabilityConfiguration.SectionName)
                                                       .Get<ObservabilityConfiguration>()
                                                   ?? throw new InvalidOperationException($"The '{AppConfiguration.RootSection}:{ObservabilityConfiguration.SectionName}' configuration section is missing.");

        if (string.IsNullOrWhiteSpace(observability.OtlpEndpoint))
            throw new InvalidOperationException($"{AppConfiguration.RootSection}:{ObservabilityConfiguration.SectionName}:OtlpEndpoint is not configured.");

        if (string.IsNullOrWhiteSpace(observability.GrafanaInstanceId))
            throw new InvalidOperationException($"{AppConfiguration.RootSection}:{ObservabilityConfiguration.SectionName}:GrafanaInstanceId is not configured.");

        if (string.IsNullOrWhiteSpace(observability.GrafanaAccessPolicyToken))
            throw new InvalidOperationException($"{AppConfiguration.RootSection}:{ObservabilityConfiguration.SectionName}:GrafanaAccessPolicyToken is not configured.");

        services.AddOptions<ObservabilityConfiguration>()
            .Bind(configuration.AppSection(ObservabilityConfiguration.SectionName))
            .ValidateOnStart();

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
        // parameter values, fine in a dev console but not for shared Grafana.
        //
        // Scoped to the OpenTelemetry provider: the provider-less AddFilter
        // overload applies to every provider, silencing the dev console and
        // overriding Logging:LogLevel. The console levels live in configuration
        // (Warning in appsettings.json, Information in
        // appsettings.Development.json), because Program.cs does not call this
        // registration until Grafana is configured.
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
