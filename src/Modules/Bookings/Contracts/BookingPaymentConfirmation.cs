using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation -
// Transactions should only ever reach this through
// IBookingPaymentConfirmation, resolved via DI.
internal class BookingPaymentConfirmation(AppBookingsDbContext dbContext) : IBookingPaymentConfirmation
{
    public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await dbContext.Bookings
                              .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), bookingId);

        // Checked explicitly rather than letting Booking.Confirm()'s own
        // guard throw for this case - a cancelled-before-payment-resolved
        // booking is an expected outcome the caller needs to react to
        // (start a refund), not an error to propagate.
        if (booking.BookingStatus == BookingStatus.Cancelled)
        {
            return false;
        }

        booking.Confirm();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
