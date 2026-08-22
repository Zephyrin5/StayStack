using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Bookings.Entities;
using Catalog.Contracts;
using Mediator;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<ConfirmBookingRequest, ConfirmBookingResponse>
{
    public async ValueTask<ConfirmBookingResponse> Handle(ConfirmBookingRequest request, CancellationToken cancellationToken)
    {
        // Confirms the hold first (marks it 'booked' in Catalog) - if this
        // throws (missing/already-consumed/expired), nothing in Bookings
        // has been touched yet. Two separate DbContexts/connections here,
        // same "sequential writes, narrow failure window, no distributed
        // transaction" tradeoff BecomeHostHandler already documents: if the
        // save below fails after this succeeds, the hold stays 'booked'
        // with no corresponding Booking row - rare (only a Bookings-side
        // DB failure between the two calls), and out of scope to fully
        // close for this first increment.
        ConfirmedHold hold = await holdConfirmation.ConfirmHoldAsync(request.HoldId, cancellationToken);

        UnitSummary unit = await unitLookup.GetUnitAsync(hold.UnitId, cancellationToken)
                            ?? throw new NotFoundException("Unit", hold.UnitId);

        int nights = hold.CheckOut.DayNumber - hold.CheckIn.DayNumber;
        decimal totalPrice = unit.BasePrice * nights;

        Booking booking = Booking.Create(
            hold.UnitId,
            request.HoldId,
            currentUserProvider.UserId,
            request.GuestName,
            request.GuestEmail,
            request.GuestPhone,
            hold.CheckIn,
            hold.CheckOut,
            request.GuestCount,
            totalPrice,
            unit.Currency);

        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync(cancellationToken);

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
