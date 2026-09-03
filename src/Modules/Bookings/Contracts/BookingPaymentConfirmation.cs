using Availability.Contracts;
using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation -
// Transactions should only ever reach this through
// IBookingPaymentConfirmation, resolved via DI.
internal class BookingPaymentConfirmation(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation) : IBookingPaymentConfirmation
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

        // The hold's other half of the same transition. Until payment, the
        // hold sits in 'pending_payment' and is a candidate for
        // ExpireUnpaidBookingsJob; this is what takes it out of reach of
        // that sweep and turns it into sold inventory.
        //
        // After the booking write, not before, and deliberately not in one
        // transaction with it - they are separate modules and separate
        // schemas, the same compensating-write shape as docs/adr/0003. The
        // ordering is what makes the failure mode survivable: a crash
        // between them leaves a Confirmed booking whose hold is still
        // 'pending_payment', which the expiry job skips (it only takes
        // Pending bookings), so the range stays held for a guest who paid
        // for it. The reverse order would leave a sold hold with a booking
        // still awaiting payment, which the sweep would then cancel and
        // release out from under them.
        //
        // A false result means the hold was released or expired before this
        // payment landed - the booking is Confirmed but its inventory is
        // gone, which needs a human, not a silent success. Nothing here can
        // put the range back, since another guest may already hold it.
        bool holdMarkedPaid = await holdConfirmation.MarkHoldPaidAsync(booking.HoldId, cancellationToken);
        if (!holdMarkedPaid)
        {
            throw new BookingHoldNoLongerHeldException(booking.Id, booking.HoldId);
        }

        return true;
    }
}
