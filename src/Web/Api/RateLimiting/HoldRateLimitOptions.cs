using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

public class HoldRateLimitOptions
{
    public const string SectionName = AuthRateLimitOptions.SectionName;

    [Range(1, int.MaxValue)]
    public int HoldPermitLimit { get; set; } = 20;
    [Range(1, int.MaxValue)]
    public int HoldWindowSeconds { get; set; } = 60;
}
