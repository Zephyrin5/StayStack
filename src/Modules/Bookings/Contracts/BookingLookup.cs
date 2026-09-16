using Microsoft.Extensions.Options;
using Bookings.Entities;
using SeedWork.ValueObjects;
using Bookings.Features.Common;
using Microsoft.EntityFrameworkCore;
using Bookings.Contracts;
namespace Bookings.Contracts;

// internal: Transactions and Reviews reach this only through IBookingLookup,
// resolved via DI. The management token never reaches here; it is exchanged
// for a session first (BookingAccessChecker.ResolveByManagementTokenAsync).
internal class BookingLookup(
    BookingsDb dbContext, IBookingSessions bookingSessions) : IBookingLookup
{
    public async Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Materialize first, map after - see docs/adr/0006. Applied here to
        // TotalPrice's ComplexProperty mapping the same way ADR-0006 already
        // requires for LocalizedText/CancellationPolicy - a .Select()
        // projecting a complex property straight into a different record
        // type is exactly the shape that convention exists to avoid.
        Booking? booking = await dbContext.Bookings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        return booking is null
            ? null
            : new BookingSummary
            {
                Id = booking.Id,
                TotalPrice = booking.TotalPrice,
                IsPending = booking.BookingStatus == BookingStatus.Pending
            };
    }

    public async Task<BookingAccessResult?> VerifyBookingAccessAsync(
        Guid bookingId, Guid? customerId, CancellationToken cancellationToken)
    {
        BookingAccess? access = await BookingAccessChecker.ResolveAsync(
            dbContext, bookingId, customerId,
            await bookingSessions.GetSessionBookingIdAsync(cancellationToken), cancellationToken);

        Booking? booking = access?.Booking;

        return booking is null
            ? null
            : new BookingAccessResult
            {
                BookingId = booking.Id,
                UnitId = booking.UnitId,
                CheckIn = booking.CheckIn,
                CheckOut = booking.CheckOut,
                IsConfirmed = booking.BookingStatus == BookingStatus.Confirmed,
                IsPending = booking.BookingStatus == BookingStatus.Pending,
                CancelledAt = booking.CancelledAt,
                TotalPrice = booking.TotalPrice,
                GuestEmail = booking.GuestEmail,
                CustomerId = booking.CustomerId,
                TimeZoneId = booking.TimeZoneId
            };
    }

    public async Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
        Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken)
    {
        return await dbContext.Bookings.AsNoTracking()
            .Where(b => b.CustomerId == customerId
                        && b.BookingStatus == BookingStatus.Confirmed
                        && b.CheckOut >= checkOutFrom
                        && b.CheckOut <= checkOutTo)
            .Select(b => new BookingAccessResult
            {
                BookingId = b.Id,
                UnitId = b.UnitId,
                CheckIn = b.CheckIn,
                CheckOut = b.CheckOut,
                IsConfirmed = true,
                GuestEmail = b.GuestEmail,
                CustomerId = b.CustomerId,
                TimeZoneId = b.TimeZoneId
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<RefundObligationSnapshot?> GetRefundObligationAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        RefundObligation? obligation = await dbContext.RefundObligations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.BookingId == bookingId, cancellationToken);

        return obligation is null
            ? null
            : new RefundObligationSnapshot
            {
                BookingId = obligation.BookingId,
                CancelledAt = obligation.CancelledAt,
                PolicyRefundAmount = Money.Of(obligation.PolicyRefundAmount, obligation.Currency),
                IsResolved = obligation.ResolvedAt is not null,
                Cause = obligation.Cause
            };
    }

    public Task MarkRefundObligationResolvedAsync(
        Guid bookingId, DateTimeOffset resolvedAt, CancellationToken cancellationToken) =>
        // ExecuteUpdate filtered on still-unresolved, so a repeat is a zero-row
        // no-op rather than a rewritten timestamp - the first resolution is the
        // one that happened, and moving the marker would hide a retry that
        // should be visible.
        dbContext.RefundObligations
            .Where(o => o.BookingId == bookingId && o.ResolvedAt == null)
            .ExecuteUpdateAsync(o => o.SetProperty(row => row.ResolvedAt, resolvedAt), cancellationToken);

    public Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        return dbContext.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingAccessResult
            {
                BookingId = b.Id,
                UnitId = b.UnitId,
                CheckIn = b.CheckIn,
                CheckOut = b.CheckOut,
                IsConfirmed = b.BookingStatus == BookingStatus.Confirmed,
                IsPending = b.BookingStatus == BookingStatus.Pending,
                // Read by the refund paths in Transactions to order a
                // cancellation against a payment.
                CancelledAt = b.CancelledAt,
                TotalPrice = b.TotalPrice,
                GuestEmail = b.GuestEmail,
                CustomerId = b.CustomerId,
                TimeZoneId = b.TimeZoneId
            })
            .SingleOrDefaultAsync(cancellationToken);
    }
}
