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
    ///     How long a guest-checkout management link stays usable after checkout. Anchored to checkout
    ///     rather than issuance: lead time is capped at 730 days, so an issuance-anchored TTL would
    ///     kill the token of anyone booking well in advance, before they arrived.
    ///     <para>
    ///         Must not be shorter than <see cref="ReviewWindowDaysAfterCheckOut"/>, or guest checkout
    ///         loses review access early - checked at startup.
    ///     </para>
    /// </summary>
    [Range(1, 3650)]
    public int ManagementTokenLifetimeDaysAfterCheckOut { get; set; } = 90;

    /// <summary>
    ///     How long a guest has to pay before the booking is expired and the unit returned to
    ///     inventory. A confirmed checkout keeps blocking its range, so without a deadline an
    ///     anonymous caller could take a calendar apart by submitting checkout forms: with a hold rate
    ///     limit of R, nobody holds more than R × this window at a time.
    ///     <para>
    ///         The Range's upper bound is far tighter than the other two settings here - this is an
    ///         availability control, and a deployment able to set it to a week would restore the
    ///         unbounded claim it exists to prevent.
    ///     </para>
    /// </summary>
    [Range(1, 1440)]
    public int PaymentWindowMinutes { get; set; } = 30;

    /// <summary>
    ///     How long a completed checkout stays replayable through its Idempotency-Key. Here rather
    ///     than a constant on CheckoutIdempotencyRecord because the request path and
    ///     PurgeReplayedCheckoutsJob's cron must agree with it.
    ///     <para>
    ///         Short because the record holds a live management token, and this window is the whole
    ///         duration of that exposure.
    ///     </para>
    /// </summary>
    [Range(1, 168)]
    public int CheckoutReplayWindowHours { get; set; } = 24;

    /// <summary>
    ///     How many sweep attempts an unresolved refund obligation may take before it is logged as
    ///     stalled.
    ///     <para>
    ///         Every obligation with nothing owed is settled where it is written, so a row the sweep
    ///         keeps seeing is waiting on a payment that has neither succeeded nor failed. The backoff
    ///         reaches its six-hour ceiling around the eighth attempt; the default of 12 is therefore
    ///         "still open a day and a half later", which is an operational question about the payment
    ///         provider rather than about this job.
    ///     </para>
    /// </summary>
    [Range(1, 1000)]
    public int RefundSweepStalledAfterAttempts { get; set; } = 12;
}
