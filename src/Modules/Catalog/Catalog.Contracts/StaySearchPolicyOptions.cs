using System.ComponentModel.DataAnnotations;
namespace Catalog.Contracts;

/// <summary>
///     How far ahead a stay can start, and how long it can run. One value each, shared by search and
///     the hold path: separate values fail asymmetrically, and one direction is silent - search
///     looser than hold is a 400 at the moment of booking, search tighter hides bookable inventory.
///     <para>
///         In <c>Catalog.Contracts</c>, the upstream side of the pair (docs/adr/0004): Bookings
///         already references it, and the reverse reference would make the modules mutually
///         dependent.
///     </para>
///     <para>
///         One asymmetry a shared value cannot remove: the hold path anchors "today" in the
///         property's own time zone (docs/adr/0018) and search anchors it in UTC, because search spans
///         every zone at once. Near the boundary they disagree by a day, in a direction that depends
///         on the property's offset, so <c>GetPropertiesRequestValidator</c> allows one day past
///         <see cref="MaxLeadTimeDays"/> - search is then never stricter than the hold, which turns
///         the silent failure (bookable inventory invisible) into the loud one (a clear 400).
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
    ///         It bounds where a window starts, not how wide it is: a window's distance from today
    ///         says nothing about how many bookings fall inside it. <see cref="MaxStayNights"/> is the
    ///         one that bounds the set search has to consider.
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
