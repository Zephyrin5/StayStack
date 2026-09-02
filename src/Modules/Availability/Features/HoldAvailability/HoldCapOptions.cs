using System.ComponentModel.DataAnnotations;
namespace Availability.Features.HoldAvailability;

/// <summary>
///     Its own section, not a sibling key under RateLimiting where it started.
///     A fixed-window limiter caps request *rate*; this caps concurrent held
///     inventory, and the two are different enough that the distinction is
///     spelled out at length in HoldAvailabilityHandler. Filing it under
///     rate limiting contradicted that reasoning.
///     <para>
///         Resolved per request through IOptions rather than captured at
///     startup, so the integration-test host can raise it the same way it
///     raises the rate limits (every request through TestServer shares one
///     client key, so a production-sized cap would make the shared suite
///         trip over its own accumulated holds).
///     </para>
/// </summary>
public class HoldCapOptions
{
    public const string SectionName = "Holds";

    /// <summary>
    ///     Live holds one client network may have at once. Sized to bound
    ///     inventory denial without breaking a NAT'd office sharing one
    ///     address - see docs/adr/0016 for the tradeoff.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxActiveHoldsPerClient { get; set; } = 25;
}
