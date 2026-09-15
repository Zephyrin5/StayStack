using System.ComponentModel.DataAnnotations;
namespace Catalog.Contracts;

/// <summary>
///     How far ahead a stay can start, and how long it can run. One value
///     each, shared by the search path and the hold path, because a search
///     that returns a property the guest then cannot hold is a dead end they
///     only discover after picking dates and clicking through.
///     <para>
///         With separate values the failure is asymmetric and one direction is
///         silent: search looser than hold means a 400 at the moment of booking,
///         search tighter means bookable inventory is invisible.
///     </para>
///     <para>
///         Lives in <c>Catalog.Contracts</c>, the upstream side of the pair
///         (docs/adr/0004): Bookings already references it for
///         <c>IUnitLookup.ResolveStayPricingAsync</c>, while the reverse reference
///         would make the modules mutually dependent. Not
///         <c>BuildingBlocks</c>, which is limited to things with no business
///         meaning.
///     </para>
///     <para>
///         No startup guard on the relationship between the two numbers,
///         unlike <c>BookingLifecyclePolicyOptions</c>' deadline ordering.
///         There is no relationship: lead time and stay length are
///         independent bounds, and the agreement that actually matters - the
///         one between search and hold - is structural here rather than
///         something a check could catch drifting.
///     </para>
///     <para>
///         One residual asymmetry a shared value does not remove: the two
///         paths anchor "today" differently, and must. The hold path uses the
///         property's own time zone (docs/adr/0018); search uses UTC, because
///         it spans every property's zone at once. Near the boundary the two
///         disagree by a day, in a direction that depends on the property's
///         offset.
///     </para>
///     <para>
///         Search picks its direction: <c>GetPropertiesRequestValidator</c>
///         allows one day past <see cref="MaxLeadTimeDays"/>, so search is never
///         stricter than the hold path. Search stricter would silently hide a
///         bookable property; search looser gives a clear 400 from the hold. The
///         extra day cannot escape the bound: a local date is within one day of
///         the UTC date in every zone, so anything search admits, the
///         property's own clock rejects at most a day later.
///     </para>
/// </summary>
public class StaySearchPolicyOptions
{
    public const string SectionName = "StaySearchPolicy";

    /// <summary>
    ///     How far in the future a check-in date may be. Without it an
    ///     anonymous caller could hold a unit for [today, today+3650) and the
    ///     exclusion constraint would enforce that decade-long block - the
    ///     damage bound in docs/adr/0016, and a product rule about how far
    ///     ahead this platform sells.
    ///     <para>
    ///         Purely those two things. It is *not* what bounds the
    ///         blocked-unit set <c>GetPropertiesHandler</c> materializes,
    ///         though it is easy to assume from where the two are enforced:
    ///         this bounds where a window starts, and a window's distance
    ///         from today says nothing about how many bookings fall inside
    ///         it. See <see cref="MaxStayNights"/>, which is the one that
    ///         does.
    ///     </para>
    /// </summary>
    [Range(1, 3650)]
    public int MaxLeadTimeDays { get; set; } = 730;

    /// <summary>
    ///     Longest stay, in nights, that can be held or searched for.
    ///     <para>
    ///         The upper end of the Range is deliberately far tighter than
    ///         MaxLeadTimeDays'. <c>GetPropertiesHandler</c> depends on this
    ///         for more than product policy: on its own, it is what keeps the
    ///         blocked-unit set Availability returns proportional to real
    ///         occupancy in a bounded window rather than to every unit ever
    ///         booked. That set is the units booked across the requested
    ///         window, so it scales with the window's width - which is this
    ///         value and nothing else. A deployment able to set this to a
    ///         decade could silently undo that, which is why the Range here
    ///         is tight even though a long stay is otherwise harmless.
    ///     </para>
    /// </summary>
    [Range(1, 365)]
    public int MaxStayNights { get; set; } = 90;
}
