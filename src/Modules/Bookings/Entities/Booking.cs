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
        decimal subtotal,
        BookingStatus bookingStatus,
        CancellationPolicy cancellationPolicy)
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
        Subtotal = subtotal;
        BookingStatus = bookingStatus;
        CancellationPolicy = cancellationPolicy;
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

    // The one Money-typed (currency-carrying) field on this entity -
    // Subtotal below is a plain decimal in this same currency by
    // construction (a booking has exactly one currency), matching
    // UnitAvailabilityHold.Subtotal's own reasoning (see docs/adr/0015).
    public Money TotalPrice { get; private set; }

    // Snapshotted directly from the hold's own Subtotal at confirm time
    // (ConfirmBookingHandler), not reconstructed - see
    // ConfirmedHold.Subtotal's own doc comment for why reconstruction was
    // the actual rounding bug this closes.
    public decimal Subtotal { get; private set; }

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
        decimal subtotal,
        CancellationPolicy cancellationPolicy)
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
        Guard.Against.Negative(totalPrice.Amount);
        Guard.Against.Negative(subtotal);
        Guard.Against.Null(cancellationPolicy);

        return new Booking(
            id, unitId, holdId, customerId, guestName, guestEmail, guestPhone,
            checkIn, checkOut, guestCount, totalPrice, subtotal, BookingStatus.Pending, cancellationPolicy);
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
