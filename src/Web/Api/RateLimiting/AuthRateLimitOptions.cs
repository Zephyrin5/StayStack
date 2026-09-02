namespace Api.RateLimiting;

public class AuthRateLimitOptions
{
    // Shared with HoldRateLimitOptions: both bind the same section, whose
    // sibling keys are prefixed per policy.
    public const string SectionName = "RateLimiting";

    public int AuthPermitLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
}
