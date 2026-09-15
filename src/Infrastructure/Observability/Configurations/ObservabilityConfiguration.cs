namespace Observability.Configurations;

public class ObservabilityConfiguration
{
    public const string SectionName = "Observability";

    // set, not init: EnableConfigurationBindingGenerator's binder skips init-only
    // setters without complaint, leaving every value at its default (see
    // LocalizationSettings).

    public string OtlpEndpoint { get; set; } = string.Empty;
    public string GrafanaInstanceId { get; set; } = string.Empty;
    public string GrafanaAccessPolicyToken { get; set; } = string.Empty;

    public bool CommandTracingEnabled { get; set; } = true;
}
