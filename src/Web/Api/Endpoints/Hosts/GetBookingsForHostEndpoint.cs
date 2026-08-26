using Bookings.Features.GetBookingsForHost;
using Bookings.Features.GetHostBookings;
using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Hosts;

public class GetBookingsForHostEndpoint(IMediator mediator) : Endpoint<GetBookingsForHostRequest, PagedResponse<HostBookingSummary>>
{
    public override void Configure()
    {
        Get("{HostId}/bookings");
        Policies(AuthorizationPolicies.Administrator);
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "List bookings against a specific host's properties (admin-only)";
            s.Description = "The admin-targeted counterpart to GET /api/bookings/host - same response " +
                            "shape, but for a host named by id rather than the caller's own. Paginated - " +
                            "defaults to page 1, 20 per page.";
            s.Response<PagedResponse<HostBookingSummary>>(200, "Bookings returned.");
            s.Response<ProblemDetails>(404, "Host not found.");
        });
    }

    public override async Task HandleAsync(GetBookingsForHostRequest req, CancellationToken ct)
    {
        PagedResponse<HostBookingSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
