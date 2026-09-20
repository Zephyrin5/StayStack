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
    ///     CustomerId (authenticated) or a booking session naming this same
    ///     booking (guest checkout) - null otherwise. The management token is
    ///     not accepted here; <see cref="ResolveByManagementTokenAsync"/>
    ///     exchanges it for a session. Doesn't distinguish "doesn't exist" from
    ///     "isn't yours", same reasoning as IHostAuthorization.RequireOwnership.
    ///     Neither path carries an expiry of its own: the session's was enforced
    ///     by the authentication handler, and a CustomerId is account-based
    ///     proof rather than a bearer credential that could leak.
    /// </summary>
    public static async Task<BookingAccess?> ResolveAsync(
        BookingsDb dbContext,
        Guid bookingId,
        Guid? customerId,
        Guid? sessionBookingId,
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
            return new BookingAccess(booking, BookingAccessKind.Account);
        }

        // A booking session. The equality check is the whole authorization
        // decision and belongs here rather than at the edge: the session proves
        // "the bearer once held the management token for booking X", which says
        // nothing about the booking this request names. A session for A acting
        // on B is the obvious attack.
        //
        // No expiry check: the session's `exp` was enforced by the
        // authentication handler, which is why the caller passes a resolved id
        // rather than a raw token.
        if (sessionBookingId == bookingId)
        {
            return new BookingAccess(booking, BookingAccessKind.Link);
        }

        return null;
    }

    /// <summary>
    ///     The management-token path, reachable from exactly one caller:
    ///     CreateBookingSessionHandler, which trades the token for a session.
    ///     <para>
    ///         Separate from <see cref="ResolveAsync"/> rather than an optional parameter on it: a
    ///         parameter every other caller passes null to is an invitation to start passing
    ///         something, and this validates the long-lived credential. There is no
    ///         authenticated-customer path either - a signed-in owner's access token already proves
    ///         ownership everywhere a session would.
    ///     </para>
    /// </summary>
    public static async Task<BookingAccess?> ResolveByManagementTokenAsync(
        BookingsDb dbContext,
        Guid bookingId,
        string managementToken,
        TimeProvider timeProvider,
        int managementTokenLifetimeDaysAfterCheckOut,
        CancellationToken cancellationToken)
    {
        Booking? booking = await dbContext.Bookings
            .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking is null || string.IsNullOrEmpty(managementToken))
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
        if (today > booking.CheckOut.AddDays(managementTokenLifetimeDaysAfterCheckOut))
        {
            return null;
        }

        string tokenHash = SecureToken.Hash(managementToken);
        bool tokenMatches = await dbContext.BookingManagementTokens
            .AnyAsync(t => t.BookingId == bookingId && t.TokenHash == tokenHash, cancellationToken);

        return tokenMatches ? new BookingAccess(booking, BookingAccessKind.Link) : null;
    }
}

/// <summary>
///     A resolved booking together with <em>how</em> the caller proved they
///     owned it. The second half is not bookkeeping: a destructive action can
///     reasonably ask more of a caller holding a link than of one holding an
///     account, and it cannot make that distinction unless the check that
///     resolved the booking reports it.
/// </summary>
internal sealed record BookingAccess(Booking Booking, BookingAccessKind Kind);

internal enum BookingAccessKind
{
    /// <summary>
    ///     A matching CustomerId. Proof of ownership that cannot be forwarded,
    ///     screenshotted or read out of a URL.
    /// </summary>
    Account,

    /// <summary>
    ///     A management token, or a session exchanged for one. Both descend
    ///     from the same link, so they are one kind rather than two -
    ///     otherwise exchanging the token for a session would launder away
    ///     whatever extra proof the link path is asked for, which is precisely
    ///     the hole a second factor exists to close.
    /// </summary>
    Link
}
