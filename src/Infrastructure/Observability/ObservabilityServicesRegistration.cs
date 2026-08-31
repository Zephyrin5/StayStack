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

        // Filter out EF Core's own command-level logs from what gets
        // exported. EnableSensitiveDataLogging on the DbContext means
        // this category logs full SQL parameter values (usernames today,
        // potentially worse later) - useful in the console during dev,
        // not something that should ship to a shared Grafana instance.
        services.AddLogging(logging =>
            logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None));

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
                .AddMeter(CommandTelemetry.SourceName)
                .AddMeter(OutboxTelemetry.MeterName)
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
