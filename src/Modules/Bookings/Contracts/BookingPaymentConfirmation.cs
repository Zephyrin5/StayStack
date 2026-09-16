using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
namespace Bookings.Contracts;

internal class BookingPaymentConfirmation(
    BookingsDb dbContext,
    IHoldConfirmation holdConfirmation) : IBookingPaymentConfirmation
{
    public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Commits with the caller's payment, so a payment sells the range and confirms the stay, or neither.
        DbTransaction transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
                                    ?? throw new InvalidOperationException(
                                        $"{nameof(BookingPaymentConfirmation)}.{nameof(ConfirmPaymentAsync)} must run inside the caller's " +
                                        "atomic scope: a confirmation committed on its own survives the payment it records rolling back.");

        Guid holdId = await dbContext.Bookings.AsNoTracking()
                          .Where(b => b.Id == bookingId)
                          .Select(b => (Guid?)b.HoldId)
                          .SingleOrDefaultAsync(cancellationToken)
                      ?? throw new NotFoundException(nameof(Booking), bookingId);

        // FOR UPDATE, not SKIP LOCKED: a payment waits to learn whether expiry or cancellation won (docs/adr/0028).
        await dbContext.Database.GetDbConnection().ExecuteScalarAsync<Guid?>(new CommandDefinition(
            $"""SELECT id FROM {BookingsModel.Schema}.bookings WHERE id = @BookingId FOR UPDATE""",
            new { BookingId = bookingId },
            transaction,
            cancellationToken: cancellationToken));

        Booking booking = await dbContext.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), bookingId);

        // A cancelled booking's hold may already be someone else's: report it, and the caller refunds.
        if (booking.BookingStatus == BookingStatus.Cancelled)
        {
            return false;
        }

        // False when the hold was released or expired first: the range is no longer this booking's to sell.
        if (!await holdConfirmation.MarkHoldPaidAsync(holdId, cancellationToken))
        {
            return false;
        }

        booking.Confirm();
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}
