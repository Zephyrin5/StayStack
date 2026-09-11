using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Security;
using BuildingBlocks.Time;
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
    ///     CustomerId (authenticated), a booking session naming this same
    ///     booking, or a matching, not-yet-expired BookingManagementToken hash
    ///     (guest checkout) - null otherwise.
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
        Guid? sessionBookingId,
        TimeProvider timeProvider,
        int managementTokenLifetimeDaysAfterCheckOut,
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

        // A booking session, exchanged for a management token earlier. The
        // equality check is the whole authorization decision and it has to be
        // here rather than at the edge: the token proves "the bearer once held
        // the management token for booking X", which says nothing about the
        // booking this request names. A session for A acting on B is the
        // obvious attack, and it is a one-line mistake to leave open.
        //
        // No expiry check of its own - the token's own `exp` was enforced by
        // the authentication handler before this ever ran, which is why the
        // caller passes a resolved id rather than a raw token.
        //
        // Checked before the management-token path, not after: a caller
        // holding a session should never be made to present the long-lived
        // credential again, which is the entire point of exchanging it.
        if (sessionBookingId == bookingId)
        {
            return booking;
        }

        if (string.IsNullOrEmpty(managementToken))
        {
            return null;
        }

        // Resolved here rather than taken as a parameter, and from the
        // booking's own snapshot: the timezone isn't knowable until the
        // booking is loaded, and CheckOut below is a property-local date, so
        // comparing it against a UTC "today" compares unlike with unlike.
        // See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);

        // A leaked management link shouldn't stay valid forever. Anchored to
        // checkout rather than issuance: lead time is capped at 730 days
        // (StaySearchPolicyOptions.MaxLeadTimeDays), so an issuance-anchored
        // TTL of a few months would kill the token of anyone booking a
        // holiday well in advance, before they ever arrived.
        //
        // This used to also be what bounded the review window - the comment
        // here said so - which meant the review deadline applied to guest
        // checkout only, and tightening this for security reasons would have
        // silently shortened it. Reviews now has its own explicit window
        // (BookingLifecyclePolicyOptions), so this is purely a question about
        // how long a bearer link should live.
        if (today > booking.CheckOut.AddDays(managementTokenLifetimeDaysAfterCheckOut))
        {
            return null;
        }

        string tokenHash = SecureToken.Hash(managementToken);
        bool tokenMatches = await dbContext.BookingManagementTokens
            .AnyAsync(t => t.BookingId == bookingId && t.TokenHash == tokenHash, cancellationToken);

        return tokenMatches ? booking : null;
    }
}
