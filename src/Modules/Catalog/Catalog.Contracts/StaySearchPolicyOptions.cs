using System.ComponentModel.DataAnnotations;
namespace Catalog.Contracts;

/// <summary>
///     How far ahead a stay can start, and how long it can run. One value
///     each, shared by the search path and the hold path, because a search
///     that returns a property the guest then cannot hold is a dead end they
///     only discover after picking dates and clicking through.
///     <para>
///         These were four constants - <c>MaxLeadTimeDays</c> and
///         <c>MaxStayNights</c> defined once in Availability for holds and
///         again in Catalog for search - with nothing tying them together.
///         The failure was asymmetric and one direction silent: search looser
///         than hold means a 400 at the moment of booking, search tighter
///         means bookable inventory is invisible with nothing to say why.
///         Neither is representable now, since there is one number rather
///         than an agreement between two.
///     </para>
///     <para>
///         Lives in <c>Catalog.Contracts</c> rather than
///         <c>Availability.Contracts</c>, despite Availability being the
///         module that ultimately enforces this at hold time, because the
///         module order in docs/adr/0004 is
///         <c>Hosts → Catalog → Availability → …</c>: Availability already
///         references <c>Catalog.Contracts</c> (for
///         <c>IUnitLookup.ResolveStayPricingAsync</c>), so this home costs
///         nothing, while the reverse reference would make the two modules
///         mutually dependent - the exact cycle that ADR's direction rule
///         exists to prevent, and which it records happening for real once
///         already. Same reasoning that put
///         <c>BookingLifecyclePolicyOptions</c> in <c>Bookings.Contracts</c>
///         rather than in <c>BuildingBlocks</c>: the upstream side of a pair
///         that already depends on it, not a neutral project, and not
///         <c>BuildingBlocks</c>, which is deliberately limited to things
///         with no business meaning.
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
///         One residual asymmetry worth knowing, which a shared value does
///         not remove: the two paths anchor "today" differently, and must.
///         <c>HoldAvailabilityHandler</c> uses the property's own time zone
///         (docs/adr/0018), while <c>GetPropertiesHandler</c> uses UTC,
///         because a search spans every property's zone at once and has no
///         single one to resolve against. At the exact boundary that can
///         differ by a day in either direction. That is a property of the
///         anchor, not of these values.
///     </para>
/// </summary>
public class StaySearchPolicyOptions
{
    public const string SectionName = "StaySearchPolicy";

    /// <summary>
    ///     How far in the future a check-in date may be. Without it an
    ///     anonymous caller could hold a unit for [today, today+3650) and the
    ///     exclusion constraint would enforce that decade-long block, or ask
    ///     search for a window wide enough that the blocked-unit set it
    ///     materializes stops being bounded by anything useful.
    /// </summary>
    [Range(1, 3650)]
    public int MaxLeadTimeDays { get; set; } = 730;

    /// <summary>
    ///     Longest stay, in nights, that can be held or searched for.
    ///     <para>
    ///         The upper end of the Range is deliberately far tighter than
    ///         MaxLeadTimeDays'. <c>GetPropertiesHandler</c> depends on this
    ///         for more than product policy: together with the lead-time
    ///         bound it is what keeps the blocked-unit set Availability
    ///         returns proportional to real occupancy in a bounded window
    ///         rather than to every unit ever booked. A deployment able to
    ///         set this to a decade could silently undo that.
    ///     </para>
    /// </summary>
    [Range(1, 365)]
    public int MaxStayNights { get; set; } = 90;
}
