using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

/// <summary>
///     The "reads" policy: the anonymous read endpoints - GetProperties,
///     GetPropertyById, GetPriceCalendar and GetPropertyReviews - which were
///     unauthenticated and unthrottled.
///     <para>
///         Their per-request cost is already bounded (the search window caps
///         in StaySearchPolicyOptions, the price calendar's date bounds,
///         PaginationDefaults.MaxOffset, and the HybridCache payload and size
///         limits). None of that bounds the <em>rate</em>: a caller could
///         issue them as fast as the network allowed, and each cache miss
///         still costs a cross-module availability call, a pricing-rule load
///         and, for the calendar, a generate_series cross join.
///     </para>
/// </summary>
public class ReadRateLimitOptions : IFixedWindowLimit
{
    // Its own section - see AuthRateLimitOptions.SectionName.
    public const string SectionName = "RateLimiting:Reads";

    /// <summary>
    ///     Far more generous than the auth (10) and hold (20) policies, because the failure modes are
    ///     not symmetric: the partition is a client network, so tripping this breaks browsing for
    ///     every guest behind one NAT who has done nothing wrong, invisibly from their side.
    ///     <para>
    ///         Set where no plausible browsing session reaches it - a property page fetches a
    ///         calendar, reviews and the listing, so a busy minute is tens of requests - while still
    ///         turning "as fast as the network allows" into a bounded number. A ceiling, not a quota.
    ///     </para>
    ///     <para>Per instance, not per deployment - see FixedWindowPolicies.</para>
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 300;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
