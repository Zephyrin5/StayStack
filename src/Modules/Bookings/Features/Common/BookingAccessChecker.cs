using Bookings.Entities;
using BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Features.Common;

/// <summary>
///     The one place "does this caller own this booking" is actually
///     decided - used by CancelBookingHandler, GetBookingForManagementHandler,
///     and BookingLookup.VerifyBookingAccessAsync (the cross-module surface
///     Reviews calls through), instead of three copies of the same check.
///     Internal to Bookings: in-module callers use this directly, Reviews
///     reaches the same logic only through Bookings.Contracts.IBookingLookup.
/// </summary>
internal static class BookingAccessChecker
{
    /// <summary>
    ///     Resolves the booking if the caller owns it - via a matching
    ///     CustomerId (authenticated) or a matching BookingManagementToken
    ///     hash (guest checkout) - null otherwise. Doesn't distinguish
    ///     "doesn't exist" from "isn't yours" in its return, same
    ///     "doesn't exist and exists-but-isn't-yours must look identical"
    ///     reasoning as IHostAuthorization.RequireOwnership. A guest-checkout
    ///     booking (CustomerId null) can only ever be resolved via the
    ///     token path - an anonymous caller with no token, or an
    ///     authenticated caller whose id doesn't match, both get null the
    ///     same way.
    /// </summary>
    public static async Task<Booking?> ResolveAsync(
        AppBookingsDbContext dbContext,
        Guid bookingId,
        Guid? customerId,
        string? managementToken,
        CancellationToken cancellationToken)
    {
        Booking? booking = await dbContext.Bookings
            .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking is null)
        {
            return null;
        }

        if (customerId is not null && booking.CustomerId == customerId)
        {
            return booking;
        }

        if (string.IsNullOrEmpty(managementToken))
        {
            return null;
        }

        string tokenHash = SecureToken.Hash(managementToken);
        bool tokenMatches = await dbContext.BookingManagementTokens
            .AnyAsync(t => t.BookingId == bookingId && t.TokenHash == tokenHash, cancellationToken);

        return tokenMatches ? booking : null;
    }
}
