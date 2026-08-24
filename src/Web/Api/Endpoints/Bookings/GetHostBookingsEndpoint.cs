using Bookings.Features.GetHostBookings;
using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class GetHostBookingsEndpoint(IMediator mediator) : Endpoint<GetHostBookingsRequest, PagedResponse<HostBookingSummary>>
{
    public override void Configure()
    {
        Get("host");
        Policies(AuthorizationPolicies.Host);
        Group<BookingsGroup>();

        Summary(s =>
        {
            s.Summary = "List bookings made against the caller's own properties";
            s.Description = "Requires the caller to be a host - HostId is derived from the caller's token, " +
                            "never accepted as input. Includes guest contact details, unlike /mine. Most " +
                            "recent first, paginated (defaults to page 1, 20 per page).";
            s.Response<PagedResponse<HostBookingSummary>>(200, "Bookings returned.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(GetHostBookingsRequest req, CancellationToken ct)
    {
        PagedResponse<HostBookingSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
