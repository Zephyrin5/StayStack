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
    // A leaked management link shouldn't stay valid forever - bounded to
    // the reservation lifecycle plus a grace window covering post-stay
    // disputes and the review window, rather than a fixed TTL from
    // issuance like a refresh token: HoldAvailabilityHandler places no
    // upper bound on how far out CheckIn can be booked, so a
    // CreatedAt-based expiry could lapse before the stay even happens.
    private const int ManagementTokenLifetimeDaysAfterCheckOut = 90;

    /// <summary>
    ///     Resolves the booking if the caller owns it - via a matching
    ///     CustomerId (authenticated) or a matching, not-yet-expired
    ///     BookingManagementToken hash (guest checkout) - null otherwise.
    ///     Doesn't distinguish "doesn't exist" from "isn't yours" (nor from
    ///     "token expired"), same reasoning as
    ///     IHostAuthorization.RequireOwnership. A guest-checkout booking
    ///     (CustomerId null) can only be resolved via the token path - no
    ///     token, a mismatched id, and a correct-but-expired token all get
    ///     null the same way. The authenticated-CustomerId path has no
    ///     expiry of its own - account-based proof of ownership, not a
    ///     bearer credential that could leak.
    /// </summary>
    public static async Task<Booking?> ResolveAsync(
        AppBookingsDbContext dbContext,
        Guid bookingId,
        Guid? customerId,
        string? managementToken,
        DateOnly today,
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

        if (today > booking.CheckOut.AddDays(ManagementTokenLifetimeDaysAfterCheckOut))
        {
            return null;
        }

        string tokenHash = SecureToken.Hash(managementToken);
        bool tokenMatches = await dbContext.BookingManagementTokens
            .AnyAsync(t => t.BookingId == bookingId && t.TokenHash == tokenHash, cancellationToken);

        return tokenMatches ? booking : null;
    }
}
