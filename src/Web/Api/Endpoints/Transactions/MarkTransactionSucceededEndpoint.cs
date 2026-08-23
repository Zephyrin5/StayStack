using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Transactions.Features.MarkTransactionSucceeded;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

// Administrator-gated as a stand-in for a real payment gateway's signed
// webhook - this asserts "money actually moved", not "act on my own
// behalf", so it should never be reachable by an end user the way
// InitiateTransactionEndpoint is. Replace this policy with real webhook
// signature verification once a provider adapter exists.
public class MarkTransactionSucceededEndpoint(IMediator mediator)
    : Endpoint<MarkTransactionSucceededRequest, MarkTransactionSucceededResponse>
{
    public override void Configure()
    {
        Post("{TransactionId}/succeed");
        Policies(AuthorizationPolicies.Administrator);
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "Mark a transaction succeeded and confirm its booking (admin-only stand-in for a gateway webhook)";
            s.Response<MarkTransactionSucceededResponse>(200, "Transaction succeeded; booking confirmed.");
            s.Response<ProblemDetails>(404, "Transaction not found.");
            s.Response<ProblemDetails>(409, "Transaction already finalized, or its booking is no longer payable.");
        });
    }

    public override async Task HandleAsync(MarkTransactionSucceededRequest req, CancellationToken ct)
    {
        MarkTransactionSucceededResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
