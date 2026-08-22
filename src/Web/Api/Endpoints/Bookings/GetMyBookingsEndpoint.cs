using Bookings.Features.GetMyBookings;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class GetMyBookingsEndpoint(IMediator mediator) : EndpointWithoutRequest<GetMyBookingsResponse>
{
    public override void Configure()
    {
        Get("mine");
        Group<BookingsGroup>();

        Summary(s =>
        {
            s.Summary = "List the caller's own bookings";
            s.Description = "Requires authentication - guest-checkout bookings (no CustomerId) never show up " +
                            "here for anyone, since there's no account to list them against. Most recent first.";
            s.Response<GetMyBookingsResponse>(200, "Bookings returned.");
            s.Response<ProblemDetails>(401, "Not authenticated.");
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        GetMyBookingsResponse result = await mediator.Send(new GetMyBookingsRequest(), ct);
        await Send.OkAsync(result, ct);
    }
}
