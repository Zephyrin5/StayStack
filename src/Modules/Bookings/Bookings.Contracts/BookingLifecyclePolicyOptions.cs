using System.ComponentModel.DataAnnotations;
namespace Bookings.Contracts;

/// <summary>
///     Two post-stay deadlines that have to be set together.
///     <para>
///         In Bookings.Contracts because checkout dates, review windows and
///         management-token lifetimes are Bookings concepts, and Reviews already
///         references this assembly for IBookingLookup. Five places decide
///         something from these values - <c>CreateStayReviewHandler</c>,
///         <c>CreateGuestReviewHandler</c>, <c>ListMyReviewableBookingsHandler</c>,
///         <c>GetBookingForManagementHandler</c>'s <c>CanReview</c>, and
///         <c>BookingAccessChecker</c> - and a mismatch means the UI offers a
///         review the API rejects. One shared value keeps them agreeing.
///     </para>
/// </summary>
public class BookingLifecyclePolicyOptions
{
    public const string SectionName = "BookingLifecycle";

    /// <summary>
    ///     How long after checkout a stay can still be reviewed, by either
    ///     party.
    ///     <para>
    ///         Applies to authenticated customers and guest checkouts alike, so
    ///         two people in the same room have the same rights whether or not
    ///         they have an account. Defaults to 90 days, the guest management
    ///         token's lifetime. Limiting reviews is normal for the industry
    ///         (Airbnb allows 14 days; Booking.com is in this range), for
    ///         freshness, fraud pressure on old stays, and closing disputes.
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

    /// <summary>
    ///     How long a completed checkout stays replayable through its
    ///     Idempotency-Key.
    ///     <para>
    ///         Here rather than as a <c>static readonly TimeSpan</c> on
    ///         CheckoutIdempotencyRecord, because it has to agree with
    ///         PurgeReplayedCheckoutsJob's cron and with the request path that
    ///         enforces it - and a constant that two other things must agree
    ///         with is the same drift the duplicated MaxLeadTimeDays constants
    ///         had before StaySearchPolicyOptions consolidated them. Every
    ///         other tunable here is [Range]-validated at startup; this one was
    ///         not validated at all.
    ///     </para>
    ///     <para>
    ///         Short because the record holds a live management token: this
    ///         window is the whole duration of that exposure. A day covers a
    ///         client retrying across an outage, a backgrounded mobile app, or
    ///         a reloaded checkout tab, and covers nothing else.
    ///     </para>
    /// </summary>
    [Range(1, 168)]
    public int CheckoutReplayWindowHours { get; set; } = 24;
}
