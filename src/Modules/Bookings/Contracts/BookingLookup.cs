using Bookings.Entities;
using Bookings.Features.Common;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.UnitLookup - Transactions/
// Reviews should only ever reach this through IBookingLookup, resolved via DI.
internal class BookingLookup(AppBookingsDbContext dbContext, TimeProvider timeProvider) : IBookingLookup
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
        Guid bookingId, Guid? customerId, string? managementToken, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        Booking? booking = await BookingAccessChecker.ResolveAsync(
            dbContext, bookingId, customerId, managementToken, today, cancellationToken);

        return booking is null
            ? null
            : new BookingAccessResult
            {
                BookingId = booking.Id,
                UnitId = booking.UnitId,
                CheckIn = booking.CheckIn,
                CheckOut = booking.CheckOut,
                IsConfirmed = booking.BookingStatus == BookingStatus.Confirmed,
                GuestEmail = booking.GuestEmail,
                CustomerId = booking.CustomerId
            };
    }

    public async Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
        Guid customerId, CancellationToken cancellationToken)
    {
        return await dbContext.Bookings.AsNoTracking()
            .Where(b => b.CustomerId == customerId && b.BookingStatus == BookingStatus.Confirmed)
            .Select(b => new BookingAccessResult
            {
                BookingId = b.Id,
                UnitId = b.UnitId,
                CheckIn = b.CheckIn,
                CheckOut = b.CheckOut,
                IsConfirmed = true,
                GuestEmail = b.GuestEmail,
                CustomerId = b.CustomerId
            })
            .ToListAsync(cancellationToken);
    }

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
                GuestEmail = b.GuestEmail,
                CustomerId = b.CustomerId
            })
            .SingleOrDefaultAsync(cancellationToken);
    }
}
