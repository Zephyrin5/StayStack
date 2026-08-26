using Ardalis.GuardClauses;
using Bookings.Contracts;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.Interfaces;
namespace Bookings.Entities;

public sealed class Booking : Entity, IAggregateRoot
{
    // See Property.cs (Catalog) for why materialization goes through a
    // real constructor rather than a parameterless one + `required`/`null!`.
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
        decimal totalPrice,
        Currency currency,
        BookingStatus bookingStatus)
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
        Currency = currency;
        BookingStatus = bookingStatus;
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

    public decimal TotalPrice { get; private set; }
    public Currency Currency { get; private set; }

    // Named BookingStatus, not Status - Status is already claimed by the
    // inherited Entity.Status (EntityStatus: soft-delete state), a
    // different axis entirely from this business lifecycle state.
    public BookingStatus BookingStatus { get; private set; }

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
        decimal totalPrice,
        Currency currency)
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
        Guard.Against.Negative(totalPrice);

        return new Booking(
            id, unitId, holdId, customerId, guestName, guestEmail, guestPhone,
            checkIn, checkOut, guestCount, totalPrice, currency, BookingStatus.Pending);
    }

    // A real mutation with its own invariant (can't cancel twice, can't
    // cancel a booking that's already run its course), not just a settable
    // property - matches Unit.SetBasePrice's reasoning. No caller wired up
    // to this yet (out of scope for this increment), but the entity's
    // lifecycle shouldn't wait for the endpoint that exercises it.
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
