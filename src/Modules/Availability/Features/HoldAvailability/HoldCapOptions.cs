namespace Availability.Features.HoldAvailability;

/// <summary>
///     Bound from the same "RateLimiting" configuration section as
///     Api.RateLimiting's Auth/Hold options - sibling key, same concern.
///     Resolved per request through IOptions rather than captured at
///     startup, so the integration-test host can raise it the same way it
///     raises the rate limits (every request through TestServer shares one
///     client key, so a production-sized cap would make the shared suite
///     trip over its own accumulated holds).
/// </summary>
public class HoldCapOptions
{
    /// <summary>
    ///     Live holds one client network may have at once. Sized to bound
    ///     inventory denial without breaking a NAT'd office sharing one
    ///     address - see docs/adr/0016 for the tradeoff.
    /// </summary>
    public int MaxActiveHoldsPerClient { get; set; } = 25;
}
