using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

/// <summary>
///     The "holds" policy: HoldAvailabilityEndpoint, anonymous and a real
///     database write. Bounds how many holds a caller can fire; what bounds
///     the inventory they can hold at once is HoldCapOptions, not this. See
///     docs/adr/0016.
/// </summary>
public class HoldRateLimitOptions : IFixedWindowLimit
{
    // Its own section - see AuthRateLimitOptions.SectionName.
    public const string SectionName = "RateLimiting:Holds";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 20;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
