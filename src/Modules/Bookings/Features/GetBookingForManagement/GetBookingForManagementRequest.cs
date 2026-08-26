using Mediator;
namespace Bookings.Features.GetBookingForManagement;

// Public - the route parameter alone identifies which booking, the query-
// string ManagementToken (bound automatically, same as any other GET
// request property FastEndpoints doesn't match to a route placeholder)
// proves the caller may see it, for a guest-checkout caller with no
// account. An authenticated caller's own CustomerId works instead - see
// BookingAccessChecker.
public record GetBookingForManagementRequest : IRequest<GetBookingForManagementResponse>
{
    public Guid BookingId { get; init; }
    public string? ManagementToken { get; init; }
}
