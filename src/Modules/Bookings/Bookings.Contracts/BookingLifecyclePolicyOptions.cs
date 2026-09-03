using System.ComponentModel.DataAnnotations;
namespace Bookings.Contracts;

/// <summary>
///     Two post-stay deadlines that have to be set together, and used to be
///     one number doing both jobs.
///     <para>
///         Lives in Bookings.Contracts because it is a Bookings concept:
///         checkout dates, review windows and management-token lifetimes have
///         business meaning, and BuildingBlocks is otherwise Exceptions,
///         Identity, Localization, Observability, Pagination, Security and
///         Time - things with none. It started there on the reasoning that
///         Bookings and Reviews both need it and neither may reference the
///         other (docs/adr/0004), which is true of the first half only:
///         Reviews already references Bookings.Contracts for IBookingLookup
///         and BookingAccessResult, so this home costs nothing and keeps
///         booking-lifecycle rules off every future module's dependency graph.
///         Five places decide something from these values -
///         <c>CreateStayReviewHandler</c>, <c>CreateGuestReviewHandler</c>,
///         <c>ListMyReviewableBookingsHandler</c>,
///         <c>GetBookingForManagementHandler</c>'s <c>CanReview</c>, and
///         <c>BookingAccessChecker</c> - and their own comments already warn
///         that a mismatch means "the UI offers a review the API rejects".
///         One shared value is what keeps them agreeing.
///     </para>
/// </summary>
public class BookingLifecyclePolicyOptions
{
    public const string SectionName = "BookingLifecycle";

    /// <summary>
    ///     How long after checkout a stay can still be reviewed, by either
    ///     party.
    ///     <para>
    ///         There was no such limit before, and that was not a decision -
    ///         Reviews only ever checked the lower bound ("has the stay
    ///         ended"). The effective deadline came from the guest management
    ///         token's lifetime, which meant it applied to guest checkout only:
    ///         an authenticated customer could review the same stay forever,
    ///         while a guest lost access at 90 days. Two people in the same
    ///         room on the same night had different rights depending on
    ///         whether they had an account.
    ///     </para>
    ///     <para>
    ///         Defaults to 90 days, which preserves what guest checkout
    ///         already did - so the change in behaviour is that the
    ///         authenticated path now matches it, rather than guests losing
    ///         anything. Limiting reviews is normal for the industry (Airbnb
    ///         is far stricter at 14 days; Booking.com is in this range), and
    ///         the reasons are freshness, fraud pressure on old stays, and
    ///         closing disputes.
    ///     </para>
    /// </summary>
    [Range(1, 3650)]
    public int ReviewWindowDaysAfterCheckOut { get; set; } = 90;

    /// <summary>
    ///     How long a guest-checkout management link stays usable after
    ///     checkout. Anchored to checkout rather than to issuance
    ///     deliberately: lead time is capped at
    ///     <c>StaySearchPolicyOptions.MaxLeadTimeDays</c> (730), so an
    ///     issuance-anchored TTL of a few months would kill the token of
    ///     anyone booking a holiday well in advance - before they ever
    ///     arrived.
    ///     <para>
    ///         Now purely a question about how long a leaked bearer link
    ///         should live, which is what lets it be shortened on security
    ///         grounds without silently shortening the review window as a
    ///         side effect. It must not be shorter than
    ///         <see cref="ReviewWindowDaysAfterCheckOut"/>, or guest checkout
    ///         loses review access early again - enforced at startup rather
    ///         than left to be rediscovered.
    ///     </para>
    /// </summary>
    [Range(1, 3650)]
    public int ManagementTokenLifetimeDaysAfterCheckOut { get; set; } = 90;

    /// <summary>
    ///     How long a guest has to pay before their booking is expired and
    ///     the unit returned to inventory.
    ///     <para>
    ///         Confirming a checkout takes the unit off the market - the hold
    ///         moves to 'pending_payment' and keeps blocking its range - so
    ///         without a deadline an unpaid booking held that range forever,
    ///         and an anonymous caller could take a calendar apart by
    ///         submitting checkout forms. This is what bounds that: with a
    ///         hold rate limit of R, no caller can hold more than R × this
    ///         window at a time, and the excess expires on its own.
    ///     </para>
    ///     <para>
    ///         30 minutes: long enough for a card form plus a retry or two,
    ///         and for a redirect through a bank's own 3-D Secure flow, while
    ///         short enough that a mistyped card doesn't cost an evening's
    ///         inventory. The Range's upper bound is deliberately far tighter
    ///         than the other two settings here - this one is an availability
    ///         control, not just a policy, and a deployment able to set it to
    ///         a week could quietly restore the unbounded-claim behaviour it
    ///         exists to prevent.
    ///     </para>
    /// </summary>
    [Range(1, 1440)]
    public int PaymentWindowMinutes { get; set; } = 30;
}
