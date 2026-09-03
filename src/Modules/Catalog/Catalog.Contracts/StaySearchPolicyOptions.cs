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
///         The hold path uses the property's own time zone (docs/adr/0018);
///         search uses UTC, because it spans every property's zone at once
///         and has no single one to resolve against. So near the boundary the
///         two disagree by a day, in a direction that depends on the
///         property's offset.
///     </para>
///     <para>
///         Search cannot remove that disagreement, so it picks its direction:
///         <c>GetPropertiesRequestValidator</c> allows one day past
///         <see cref="MaxLeadTimeDays"/>, which makes search never stricter
///         than the hold path. The two failure modes are not equally bad -
///         search stricter means a bookable property silently absent from
///         results, search looser means a clear 400 from the hold - and only
///         one of them is observable. The extra day cannot escape the bound
///         either: a local date is within one day of the UTC date in every
///         zone, so anything search now admits, some property's own clock
///         still rejects at most a day later.
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
