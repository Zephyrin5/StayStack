using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Transactions.Features.InitiateTransaction;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

public class InitiateTransactionEndpoint(IMediator mediator) : Endpoint<InitiateTransactionRequest, InitiateTransactionResponse>
{
    // Spelled out once per endpoint rather than four times: these four
    // summaries described the old carrier in four slightly different ways,
    // which is how three of them would have stayed accurate and one would
    // not.
    private const string SessionProof =
        "a guest-checkout caller sending a booking session as `Authorization: Bearer <sessionToken>` (see POST /bookings/{bookingId}/manage/session, which is where the management link is exchanged for one)";

    public override void Configure()
    {
        Post("");
        AllowAnonymous();
        Group<TransactionsGroup>();
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Start a transaction for a Pending booking";
            s.Description = "Public - guest checkout has no account to pay through either, same reasoning as " +
                            "ConfirmBookingEndpoint. Anonymous does not mean unauthorized: it requires " +
                            SessionProof + ", exactly as CancelBookingEndpoint does, while an authenticated " +
                            "customer is identified by their own CustomerId. No real payment gateway is " +
                            "wired up yet: this only records " +
                            "a Pending transaction ledger entry - see MarkTransactionSucceededEndpoint/" +
                            "MarkTransactionFailedEndpoint for what settles it.";
            s.Response<InitiateTransactionResponse>(200, "Transaction created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Booking not found, or the caller cannot prove ownership of it.");
            s.Response<ProblemDetails>(409, "Booking is not payable, or a transaction is already in progress for it.");
        });
    }

    public override async Task HandleAsync(InitiateTransactionRequest req, CancellationToken ct)
    {
        InitiateTransactionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
