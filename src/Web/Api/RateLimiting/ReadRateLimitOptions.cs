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
public class ReadRateLimitOptions
{
    // Its own section - see AuthRateLimitOptions.SectionName for why the
    // three policies no longer share one.
    public const string SectionName = "RateLimiting:Reads";

    /// <summary>
    ///     Far more generous than the auth (10) and hold (20) policies,
    ///     deliberately, because the failure modes are not symmetric.
    ///     <para>
    ///         The partition is the caller's IP, so everyone behind one NAT
    ///         or corporate proxy shares a budget. Tripping this breaks
    ///         browsing for real guests who have done nothing wrong, which is
    ///         a worse outcome than the abuse it prevents, and it breaks it
    ///         invisibly from their side.
    ///     </para>
    ///     <para>
    ///         This used to carry a second reason - that an unset
    ///         ForwardedHeaders:KnownProxies put every caller behind the
    ///         deployment's own proxy into a single partition, so the limit
    ///         had to be loose enough to survive that. It no longer can:
    ///         Program.cs refuses to start outside Development unless the
    ///         deployment declares its proxies, its trusted networks, or that
    ///         it has none. The value below has not been retightened on the
    ///         strength of that, since the NAT argument above stands on its
    ///         own - but the worst case it is sized against is now one shared
    ///         office, not the entire internet.
    ///     </para>
    ///     <para>
    ///         So it is set where no plausible human browsing session reaches
    ///         it - a property page fetches a calendar, reviews and the
    ///         listing itself, so a busy minute is tens of requests, not
    ///         hundreds - while still turning "as fast as the network allows"
    ///         into a bounded number. This is a ceiling, not a quota.
    ///     </para>
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 300;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
