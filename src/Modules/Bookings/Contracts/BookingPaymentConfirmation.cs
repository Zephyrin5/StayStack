using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation -
// Transactions should only ever reach this through
// IBookingPaymentConfirmation, resolved via DI.
internal class BookingPaymentConfirmation(AppBookingsDbContext dbContext) : IBookingPaymentConfirmation
{
    public async Task ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await dbContext.Bookings
                              .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), bookingId);

        booking.Confirm();

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
