using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using Microsoft.Extensions.Options;
namespace Bookings.Features.CreateBookingSession;

/// <summary>
///     Turns proof of ownership into a session, once.
///     <para>
///         The management token is a bearer credential with a lifetime
///         measured in months (see BookingAccessChecker), and until now it was
///         presented on every view, cancel, review and payment call - four
///         requests across three modules, one of them in a query string. Every
///         one of those was another place it could be logged, cached,
///         screenshotted or forwarded. The credential has not got weaker; it
///         has stopped travelling.
///     </para>
/// </summary>
public class CreateBookingSessionHandler(
    AppBookingsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
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
        // sessionBookingId is deliberately not passed. A session must not be
        // able to mint another session: that would turn a 45-minute credential
        // into an indefinitely renewable one, which is the property the short
        // lifetime exists to deny. Re-exchanging needs the original token,
        // which is exactly what the guest's link still carries.
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken,
                              sessionBookingId: null, timeProvider,
                              policy.Value.ManagementTokenLifetimeDaysAfterCheckOut, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        BookingSession session = bookingSessions.Issue(booking.Id);

        return new CreateBookingSessionResponse
        {
            SessionToken = session.SessionToken,
            ExpiresAt = session.ExpiresAt
        };
    }
}
