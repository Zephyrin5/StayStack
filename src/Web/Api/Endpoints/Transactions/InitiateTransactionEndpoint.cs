using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Transactions.Features.InitiateTransaction;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

public class InitiateTransactionEndpoint(IMediator mediator) : Endpoint<InitiateTransactionRequest, InitiateTransactionResponse>
{
    public override void Configure()
    {
        Post("");
        AllowAnonymous();
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "Start a transaction for a Pending booking";
            s.Description = "Public - guest checkout has no account to pay through either, same reasoning as " +
                            "ConfirmBookingEndpoint. No real payment gateway is wired up yet: this only records " +
                            "a Pending transaction ledger entry - see MarkTransactionSucceededEndpoint/" +
                            "MarkTransactionFailedEndpoint for what settles it.";
            s.Response<InitiateTransactionResponse>(200, "Transaction created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Booking not found.");
            s.Response<ProblemDetails>(409, "Booking is not payable, or a transaction is already in progress for it.");
        });
    }

    public override async Task HandleAsync(InitiateTransactionRequest req, CancellationToken ct)
    {
        InitiateTransactionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
