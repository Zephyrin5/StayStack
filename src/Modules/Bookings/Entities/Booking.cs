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
        string timeZoneId)
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
        string timeZoneId)
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

        return new Booking(
            id, unitId, holdId, customerId, guestName, guestEmail, guestPhone,
            checkIn, checkOut, guestCount, totalPrice, subtotal, BookingStatus.Pending, cancellationPolicy, timeZoneId);
    }

    // Idempotent - a repeated cancel (retried request, double-click) is a
    // no-op, not an error. Deliberately no "already run its course" check:
    // whether a booking is still reachable for cancellation is
    // BookingAccessChecker's call (the guest-checkout management token
    // stays valid through CheckOut + 90 days so a stay can still be
    // cancelled shortly after checkout) - Cancel() being invoked already
    // means that check passed.
    public void Cancel()
    {
        if (BookingStatus == BookingStatus.Cancelled)
        {
            return;
        }

        BookingStatus = BookingStatus.Cancelled;
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
    }
}
