using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.Extensions.Options;
namespace Bookings.Features.CreateBookingSession;

/// <summary>
///     Turns proof of ownership into a session, once.
///     <para>
///         The management token is a bearer credential with a lifetime
///         measured in months (see BookingAccessChecker). Exchanged here, it is
///         not presented on the view, cancel, review and payment calls, each of
///         which would be another place it could be logged, cached,
///         screenshotted or forwarded (docs/adr/0023).
///     </para>
/// </summary>
public class CreateBookingSessionHandler(
    BookingsDb dbContext,
    IBookingSessions bookingSessions,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy)
    : IRequestHandler<CreateBookingSessionRequest, CreateBookingSessionResponse>
{
    public async ValueTask<CreateBookingSessionResponse> Handle(
        CreateBookingSessionRequest request, CancellationToken cancellationToken)
    {
        // Exactly the check every management endpoint already runs, reused
        // rather than restated - a second copy of "may this caller touch this
        // booking" is how the two drift apart, and this is the copy that mints
        // credentials.
        //
        // The token-only path, which is what makes a session unable to mint
        // another session: an indefinitely renewable 45-minute credential is
        // exactly what the short lifetime exists to deny. Re-exchanging needs
        // the original token, which the guest's link still carries.
        BookingAccess access = await BookingAccessChecker.ResolveByManagementTokenAsync(
                                   dbContext, request.BookingId, request.ManagementToken, timeProvider,
                                   policy.Value.ManagementTokenLifetimeDaysAfterCheckOut, cancellationToken)
                               ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        Booking booking = access.Booking;

        BookingSession session = bookingSessions.Issue(booking.Id);

        return new CreateBookingSessionResponse
        {
            SessionToken = session.SessionToken,
            ExpiresAt = session.ExpiresAt
        };
    }
}
