using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Transactions.Features.MarkTransactionFailed;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

// Same Administrator stand-in reasoning as MarkTransactionSucceededEndpoint.
public class MarkTransactionFailedEndpoint(IMediator mediator)
    : Endpoint<MarkTransactionFailedRequest, MarkTransactionFailedResponse>
{
    public override void Configure()
    {
        Post("{TransactionId}/fail");
        Policies(AuthorizationPolicies.Administrator);
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "Mark a transaction failed (admin-only stand-in for a gateway webhook)";
            s.Description = "The booking is left Pending - a customer can retry with a fresh " +
                            "InitiateTransaction call.";
            s.Response<MarkTransactionFailedResponse>(200, "Transaction failed.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Transaction not found.");
            s.Response<ProblemDetails>(409, "Transaction already finalized.");
        });
    }

    public override async Task HandleAsync(MarkTransactionFailedRequest req, CancellationToken ct)
    {
        MarkTransactionFailedResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
