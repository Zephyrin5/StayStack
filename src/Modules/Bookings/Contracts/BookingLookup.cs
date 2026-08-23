using Bookings.Entities;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.UnitLookup - Transactions
// should only ever reach this through IBookingLookup, resolved via DI.
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
}
