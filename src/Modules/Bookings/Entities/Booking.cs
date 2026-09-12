using Ardalis.GuardClauses;
using Bookings.Contracts;
using SeedWork.Abstractions;
using SeedWork.Interfaces;
using SeedWork.ValueObjects;
namespace Bookings.Entities;

public sealed class Booking : Entity, IAggregateRoot
{
    // EF can't bind a ComplexProperty (Money) parameter back to the
    // entity's own mapped complex property - it only matches parameters
    // against directly-mapped scalar/converted properties by name, and
    // TotalPrice spans two columns. See Property.cs's identical
    // constructor pair and docs/adr/0015. This parameterless constructor
    // is EF's materialization fallback only; Create() below still goes
    // through the real constructor for every write. GuestName/GuestEmail
    // get real empty-string defaults only to satisfy the
    // non-nullable-reference-type check - EF overwrites them immediately
    // after construction.
    private Booking()
    {
        GuestName = string.Empty;
        GuestEmail = string.Empty;
        TimeZoneId = string.Empty;
    }

    private Booking(
        Guid id,
        Guid unitId,
        Guid holdId,
        Guid? customerId,
        string guestName,
        string guestEmail,
        string? guestPhone,
        DateOnly checkIn,
        DateOnly checkOut,
        int guestCount,
        Money totalPrice,
        Money subtotal,
        BookingStatus bookingStatus,
        CancellationPolicy cancellationPolicy,
        string timeZoneId,
        DateTimeOffset? paymentDueAt)
    {
        Id = id;
        UnitId = unitId;
        HoldId = holdId;
        CustomerId = customerId;
        GuestName = guestName;
        GuestEmail = guestEmail;
        GuestPhone = guestPhone;
        CheckIn = checkIn;
        CheckOut = checkOut;
        GuestCount = guestCount;
        TotalPrice = totalPrice;
        _subtotal = subtotal.Amount;
        BookingStatus = bookingStatus;
        CancellationPolicy = cancellationPolicy;
        TimeZoneId = timeZoneId;
        PaymentDueAt = paymentDueAt;
    }

    // Cross-module references, plain Guid rather than a real FK - same
    // pattern as Property.HostId (Catalog referencing Hosts). UnitId/HoldId
    // both come from Catalog, resolved through Catalog.Contracts, never
    // through a direct reference to Catalog's own entities.
    public Guid UnitId { get; private set; }
    public Guid HoldId { get; private set; }

    // Null for guest checkout - always present regardless: GuestName/Email/
    // Phone are a snapshot taken at booking time, not a live read of the
    // customer's account, so the booking's contact details don't shift if
    // the account's own email later changes.
    public Guid? CustomerId { get; private set; }
    public string GuestName { get; private set; }
    public string GuestEmail { get; private set; }
    public string? GuestPhone { get; private set; }

    public DateOnly CheckIn { get; private set; }
    public DateOnly CheckOut { get; private set; }
    public int GuestCount { get; private set; }

    // A booking carries exactly one currency, and TotalPrice is where it is
    // stored - see Subtotal below.
    public Money TotalPrice { get; private set; }

    // Persisted as one decimal column (the backing field, mapped in
    // BookingConfiguration) but exposed as Money, paired with the currency
    // this booking already has.
    //
    // docs/adr/0015 originally made this a bare decimal, reasoning that a
    // second currency column could only ever agree with TotalPrice's. That
    // storage argument still holds and nothing about it changed - which is
    // why there is no new column here. What did not hold is the leap from
    // "don't store it twice" to "don't type it": every consumer then had to
    // re-pair the currency by hand, and ConfirmBookingHandler literally did,
    // with Money.Of(hold.Subtotal, hold.TotalPrice.Currency). That is a
    // silent wrong-currency bug waiting for someone to pass a different
    // second argument, in the one place a type exists specifically to stop
    // it.
    //
    // Snapshotted directly from the hold's own Subtotal at confirm time
    // (ConfirmBookingHandler), not reconstructed - see ConfirmedHold.Subtotal
    // for why reconstruction was the actual rounding bug docs/adr/0015 closes.
    private decimal _subtotal;

    public Money Subtotal => Money.Of(_subtotal, TotalPrice.Currency);

    // Named BookingStatus, not Status - Status is already claimed by the
    // inherited Entity.Status (EntityStatus: soft-delete state), a
    // different axis entirely from this business lifecycle state.
    public BookingStatus BookingStatus { get; private set; }

    /// <summary>
    ///     When this booking's claim on its unit lapses if nobody pays.
    ///     <para>
    ///         Exists because confirming a checkout takes real inventory:
    ///         the hold moves to 'pending_payment' and keeps blocking its
    ///         range through the exclusion constraint. Without a deadline
    ///         that claim was permanent - an anonymous caller could submit
    ///         checkout forms and block a unit's calendar indefinitely
    ///         without ever paying, and nothing in the system could tell
    ///         those rows apart from sold ones or reclaim them.
    ///     </para>
    ///     <para>
    ///         The deadline lives here rather than on the hold because it is
    ///         a Bookings rule about a payment, and Availability has no way
    ///         to interpret it. It is also why the expiry job lives in this
    ///         module: releasing the hold without cancelling the booking
    ///         would leave a guest holding a confirmation with no inventory
    ///         behind it, and Availability cannot cancel a booking.
    ///     </para>
    ///     <para>
    ///         Null once a booking is no longer awaiting payment, and null
    ///         for bookings that predate this field - the sweep only ever
    ///         looks at Pending rows, so neither is a candidate.
    ///     </para>
    /// </summary>
    public DateTimeOffset? PaymentDueAt { get; private set; }

    /// <summary>
    ///     When this booking was cancelled. Null while it is not.
    ///     <para>
    ///         Read across the module boundary, by the refund paths, to order a
    ///         cancellation against a payment - see
    ///         Transaction.RefundOwedIsThisPathsToWrite. Entity.ModifiedAt
    ///         cannot serve: it is overwritten by every later write, so it says
    ///         when the row last changed rather than when this happened.
    ///     </para>
    ///     <para>
    ///         Set by Cancel() and never cleared, since nothing un-cancels a
    ///         booking. Null on rows cancelled before this column existed,
    ///         which the refund rule treats as "the payment came first".
    ///     </para>
    /// </summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    // Snapshotted from the unit's *current* policy at confirm time, same
    // "the terms they saw are the terms they get" reasoning as
    // TotalPrice/Currency - a host tightening their policy afterward can't
    // retroactively worsen an already-confirmed guest's terms. Nullable
    // only because a Booking confirmed before this feature existed has no
    // snapshot - never null for anything created through Create() below.
    // CancelBookingHandler falls back to CancellationPolicy.CreateDefault()
    // for that case rather than fabricating a retroactive claim.
    public CancellationPolicy? CancellationPolicy { get; private set; }

    // The property's IANA zone at confirm time, snapshotted for the same
    // reason CancellationPolicy is: a host correcting a mis-entered zone must
    // not retroactively move an existing guest's refund boundary or review
    // window. See docs/adr/0018.
    //
    // Non-nullable, unlike CancellationPolicy - deliberately. A null policy
    // falls back to CreateDefault(), a defensible business default; a null
    // zone would fall back to UTC, which is precisely the defect ADR-0018
    // exists to remove. Same snapshot pattern, different stakes, so different
    // nullability. Pre-ADR rows were backfilled by migration rather than left
    // to a runtime guess.
    public string TimeZoneId { get; private set; }

    // Takes its id rather than generating one internally - a redeemed promo
    // code needs the booking's id up front, to write the PromotionRedemption
    // row before the Booking itself is ever saved (see ConfirmBookingHandler),
    // so the caller decides the id and this stays a plain assignment rather
    // than the two disagreeing.
    public static Booking Create(
        Guid id,
        Guid unitId,
        Guid holdId,
        Guid? customerId,
        string guestName,
        string guestEmail,
        string? guestPhone,
        DateOnly checkIn,
        DateOnly checkOut,
        int guestCount,
        Money totalPrice,
        Money subtotal,
        CancellationPolicy cancellationPolicy,
        string timeZoneId,
        DateTimeOffset paymentDueAt)
    {
        Guard.Against.Default(id);
        Guard.Against.Default(unitId);
        Guard.Against.Default(holdId);
        Guard.Against.NullOrWhiteSpace(guestName);
        Guard.Against.NullOrWhiteSpace(guestEmail);
        Guard.Against.InvalidFormat(guestEmail, nameof(guestEmail),
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$", "Guest email is not a valid email address.");
        Guard.Against.InvalidInput(checkOut, nameof(checkOut),
            c => c > checkIn, "Check-out must be after check-in.");
        Guard.Against.NegativeOrZero(guestCount);

        // NegativeOrZero, not Negative: a booking has to be payable. A zero
        // total is refused by Transaction.Create's own guard, so allowing one
        // here produced a booking the guest could never pay for - stuck
        // Pending forever, failing every payment attempt. Reachable via a
        // 100% promo code, or any FixedAmount code at least as large as the
        // subtotal (ComputeDiscountAmount caps the discount there).
        //
        // This is the invariant, not the user-facing check -
        // ConfirmBookingHandler rejects the same case with a proper
        // validation message before it ever reaches here. Supporting genuinely
        // free stays would mean a confirm-without-payment path, not relaxing
        // this.
        Guard.Against.NegativeOrZero(totalPrice.Amount);
        Guard.Against.Negative(subtotal.Amount);
        Guard.Against.Null(cancellationPolicy);
        Guard.Against.NullOrWhiteSpace(timeZoneId);

        // Required, not optional: a booking created without a deadline is
        // an unbounded claim on a unit's calendar, which is the state this
        // field exists to make unrepresentable.
        Guard.Against.Default(paymentDueAt);

        return new Booking(
            id, unitId, holdId, customerId, guestName, guestEmail, guestPhone,
            checkIn, checkOut, guestCount, totalPrice, subtotal, BookingStatus.Pending, cancellationPolicy, timeZoneId,
            paymentDueAt);
    }

    /// <summary>
    ///     Whether a guest may cancel this booking themselves, as of the
    ///     given property-local date.
    ///     <para>
    ///         Being <em>allowed to reach</em> a booking and being allowed to
    ///         <em>cancel</em> it are different questions, and conflating them
    ///         is what left this open. BookingAccessChecker answers the first:
    ///         a management link stays usable until CheckOut + 90 days so a
    ///         guest can still view a finished stay, and an authenticated
    ///         customer's own booking has no time limit at all. Nothing then
    ///         asked the second question, so a guest could cancel a stay they
    ///         were in the middle of - handing the remaining nights back to
    ///         inventory - or turn a stay that ended months ago into a
    ///         Cancelled one, taking its reviewability with it and pushing a
    ///         long-settled payment into a refund workflow.
    ///     </para>
    ///     <para>
    ///         The only trace of the date was ComputeRefund's
    ///         Math.Max(daysBeforeCheckIn, 0), which clamped the refund tier
    ///         for a date already past instead of refusing the request.
    ///     </para>
    ///     <para>
    ///         Cancellation ends at check-in, not at checkout: once a stay has
    ///         started there is nothing left to cancel, only to cut short.
    ///         Leaving early is a different transaction with different money
    ///         attached, and if it becomes a product requirement it wants its
    ///         own operation rather than this one relaxed.
    ///     </para>
    /// </summary>
    public bool CanBeCancelledOn(DateOnly today)
    {
        return BookingStatus != BookingStatus.Cancelled && today < CheckIn;
    }

    // Idempotent - a repeated cancel (retried request, double-click) is a
    // no-op, not an error.
    //
    // No date guard here, deliberately: this is the state transition, and
    // CanBeCancelledOn above is the self-service policy over it. The two
    // callers need different answers - ExpireUnpaidBookingsJob cancels an
    // unpaid booking on the system's behalf and must not be subject to a
    // rule written for guests, even though in practice its bookings are
    // always still before check-in.
    //
    // Deliberately no "already run its course" check:
    // whether a booking is still reachable for cancellation is
    // BookingAccessChecker's call (the guest-checkout management token
    // stays valid through CheckOut + 90 days so a stay can still be
    // cancelled shortly after checkout) - Cancel() being invoked already
    // means that check passed.
    public void Cancel(DateTimeOffset cancelledAt)
    {
        if (BookingStatus == BookingStatus.Cancelled)
        {
            return;
        }

        BookingStatus = BookingStatus.Cancelled;
        CancelledAt = cancelledAt;
        PaymentDueAt = null;
    }

    // Called by IBookingPaymentConfirmation once a Transaction succeeds -
    // idempotent the same way Cancel() is (a retried webhook/handler call
    // shouldn't fail just because the first call already landed), but
    // throws rather than silently no-op-ing from Cancelled: a payment
    // succeeding for a booking that was cancelled out from under it is a
    // real inconsistency worth surfacing, not swallowing.
    public void Confirm()
    {
        if (BookingStatus == BookingStatus.Confirmed)
        {
            return;
        }

        if (BookingStatus != BookingStatus.Pending)
        {
            throw new BookingNotPayableException(Id);
        }

        BookingStatus = BookingStatus.Confirmed;

        // Cleared on payment: the deadline described a claim awaiting one,
        // and leaving it set would misreport a paid booking as overdue to
        // anything reading the field rather than the status.
        PaymentDueAt = null;
    }
}
