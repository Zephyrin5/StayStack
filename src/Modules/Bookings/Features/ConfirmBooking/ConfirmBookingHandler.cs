using Bookings.Entities;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<ConfirmBookingRequest, ConfirmBookingResponse>
{
    public async ValueTask<ConfirmBookingResponse> Handle(ConfirmBookingRequest request, CancellationToken cancellationToken)
    {
        // Confirms the hold first (marks it 'booked' in Catalog) - if this
        // throws (missing/already-consumed/expired), nothing in Bookings
        // has been touched yet. Cross-module write, no shared transaction -
        // see docs/adr/0003. Unlike BecomeHostHandler's own two writes,
        // though, this one has an explicit compensating rollback below if
        // the second write fails.
        //
        // Price/currency come from the hold's own snapshot (taken at
        // HoldAvailabilityHandler time), not a fresh unit lookup - the
        // price the customer saw when they held the range is the price
        // they get, even if the unit's base price changed since.
        ConfirmedHold hold = await holdConfirmation.ConfirmHoldAsync(request.HoldId, cancellationToken);

        Booking booking = Booking.Create(
            hold.UnitId,
            request.HoldId,
            currentUserProvider.UserId,
            request.GuestName,
            request.GuestEmail,
            request.GuestPhone,
            hold.CheckIn,
            hold.CheckOut,
            hold.GuestCount,
            hold.TotalPrice,
            hold.Currency);

        try
        {
            dbContext.Bookings.Add(booking);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception bookingSaveException)
        {
            // Best-effort compensation: revert the hold back to 'held' so
            // it isn't permanently stuck occupying inventory nobody ever
            // got a Booking for. Same idiom as BecomeHostHandler's
            // rollback on its second write failing.
            try
            {
                await holdConfirmation.ReleaseHoldAsync(request.HoldId, cancellationToken);
            }
            catch (Exception releaseException)
            {
                // The compensation itself failed - the hold is now stuck
                // 'booked' with nothing left to release it (until
                // ExpiredHoldsSweepJob eventually notices, once its own
                // hold_expires_at has passed). A bare `throw;` here would
                // only ever surface whichever exception happened to fire
                // last, silently losing the other - specifically, losing
                // *why the booking save failed in the first place*, which
                // is the one piece of information most needed to diagnose
                // a hold that's now stuck. Neither is dropped.
                throw new AggregateException(
                    "Booking save failed, and compensating hold release also failed.",
                    bookingSaveException, releaseException);
            }

            throw;
        }

        return new ConfirmBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            CheckIn = booking.CheckIn,
            CheckOut = booking.CheckOut,
            TotalPrice = booking.TotalPrice,
            Currency = booking.Currency
        };
    }
}
