using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

public class AuthRateLimitOptions
{
    // Shared with HoldRateLimitOptions: both bind the same section, whose
    // sibling keys are prefixed per policy.
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int AuthPermitLimit { get; set; } = 10;
    [Range(1, int.MaxValue)]
    public int AuthWindowSeconds { get; set; } = 60;
}
