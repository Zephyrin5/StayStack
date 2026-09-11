using Bookings.Contracts;
using BuildingBlocks.Security;
using Identity.Configurations;
using Identity.Features.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
namespace Api.Security;

/// <summary>
///     The Api-layer half of <see cref="IBookingSessions"/> - see that
///     interface for why it is declared in Bookings and implemented here.
/// </summary>
public class BookingSessions(
    IAuthTokenProvider tokenProvider,
    IOptions<AuthTokenConfiguration> tokenSettings,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider timeProvider) : IBookingSessions
{
    /// <summary>
    ///     The booking this session is for. A private claim name rather than
    ///     <c>sub</c>: putting a booking id in <c>sub</c> would make it a
    ///     principal identifier, and HttpContextCurrentUserProvider would then
    ///     happily read it back as a user id.
    /// </summary>
    public const string BookingIdClaim = "booking_id";

    public BookingSession Issue(Guid bookingId)
    {
        AuthTokenConfiguration settings = tokenSettings.Value;
        TimeSpan lifetime = TimeSpan.FromMinutes(settings.BookingSessionLifetimeInMinutes);

        string token = tokenProvider.GenerateScopedToken(
            settings.BookingSessionAudience,
            [new Claim(BookingIdClaim, bookingId.ToString())],
            lifetime);

        return new BookingSession
        {
            SessionToken = token,
            // Computed from the same clock and lifetime the token was signed
            // with, so a client trusting this field and a server validating
            // `exp` cannot disagree.
            ExpiresAt = timeProvider.GetUtcNow().Add(lifetime)
        };
    }

    public async Task<Guid?> GetSessionBookingIdAsync(CancellationToken cancellationToken)
    {
        HttpContext? context = httpContextAccessor.HttpContext;

        if (context is null)
        {
            return null;
        }

        // Authenticating against the named scheme is what enforces signature,
        // issuer, audience and expiry - all of it in the middleware, none of
        // it here. A handler that tried to read the claim directly off
        // context.User would find nothing: the default scheme rejects this
        // token's audience, which is exactly the property that keeps a booking
        // session from authenticating anything else.
        AuthenticateResult result = await context.AuthenticateAsync(AuthenticationSchemes.BookingSession);

        if (!result.Succeeded)
        {
            return null;
        }

        string? value = result.Principal?.FindFirst(BookingIdClaim)?.Value;

        return Guid.TryParse(value, out Guid bookingId) ? bookingId : null;
    }
}
