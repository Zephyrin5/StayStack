using Bookings.Features.CancelBooking;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class CancelBookingEndpoint(IMediator mediator) : Endpoint<CancelBookingRequest, CancelBookingResponse>
{
    public override void Configure()
    {
        Post("{BookingId}/cancel");
        AllowAnonymous();
        Group<BookingsGroup>();

        Summary(s =>
        {
            s.Summary = "Cancel the caller's own booking";
            s.Description = "Public - proves ownership one of two ways: an authenticated caller whose " +
                            "CustomerId matches, or a guest-checkout caller supplying the ManagementToken " +
                            "returned once at confirm time (see ConfirmBookingEndpoint). A booking that " +
                            "belongs to someone else, or a missing/wrong token, returns 404, not 403 or 401, " +
                            "same as every other ownership check in this API - existence and ownership " +
                            "mismatches must look identical from the outside. Idempotent: cancelling an " +
                            "already-cancelled booking succeeds without error. Releases the underlying hold " +
                            "back to available inventory. If the booking already has a Succeeded transaction, " +
                            "starts a refund; a still-Pending one is left alone, since its outcome isn't known " +
                            "yet - see POST /transactions/{id}/succeed for how a late success against an " +
                            "already-cancelled booking is handled.";
            s.Response<CancelBookingResponse>(200, "Booking cancelled.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Booking not found, belongs to someone else, or the token is missing/wrong.");
        });
    }

    public override async Task HandleAsync(CancelBookingRequest req, CancellationToken ct)
    {
        CancelBookingResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
