using Bookings.Entities;
using Bookings.Features.Common;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.UnitLookup - Transactions/
// Reviews should only ever reach this through IBookingLookup, resolved via DI.
internal class BookingLookup(AppBookingsDbContext dbContext) : IBookingLookup
{
    public Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        return dbContext.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingSummary
            {
                Id = b.Id,
                TotalPrice = b.TotalPrice,
                Currency = b.Currency,
                IsPending = b.BookingStatus == BookingStatus.Pending
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<BookingAccessResult?> VerifyBookingAccessAsync(
        Guid bookingId, Guid? customerId, string? managementToken, CancellationToken cancellationToken)
    {
        Booking? booking = await BookingAccessChecker.ResolveAsync(
            dbContext, bookingId, customerId, managementToken, cancellationToken);

        return booking is null
            ? null
            : new BookingAccessResult
            {
                BookingId = booking.Id,
                UnitId = booking.UnitId,
                CheckOut = booking.CheckOut,
                IsConfirmed = booking.BookingStatus == BookingStatus.Confirmed,
                GuestEmail = booking.GuestEmail,
                CustomerId = booking.CustomerId
            };
    }
}
