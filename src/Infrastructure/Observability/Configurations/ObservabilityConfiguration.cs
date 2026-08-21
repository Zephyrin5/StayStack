namespace Observability.Configurations;

public class ObservabilityConfiguration
{
    public string OtlpEndpoint { get; init; } = string.Empty;
    public string GrafanaInstanceId { get; init; } = string.Empty;
    public string GrafanaAccessPolicyToken { get; init; } = string.Empty;

    public bool CommandTracingEnabled { get; init; } = true;
}
