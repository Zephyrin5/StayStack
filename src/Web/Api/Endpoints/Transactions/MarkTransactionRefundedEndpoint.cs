using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Transactions.Features.MarkTransactionRefunded;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

// Same Administrator stand-in reasoning as MarkTransactionSucceededEndpoint -
// resolves the RefundPending state a cancelled booking's Succeeded
// transaction enters (see CancelBookingHandler/ITransactionReversal).
public class MarkTransactionRefundedEndpoint(IMediator mediator)
    : Endpoint<MarkTransactionRefundedRequest, MarkTransactionRefundedResponse>
{
    public override void Configure()
    {
        Post("{TransactionId}/refund");
        Policies(AuthorizationPolicies.Administrator);
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "Mark a refund succeeded (admin-only stand-in for a gateway webhook)";
            s.Description = "Only valid from RefundPending - a transaction that was never Succeeded, or " +
                            "hasn't been cancelled, has nothing to refund.";
            s.Response<MarkTransactionRefundedResponse>(200, "Transaction refunded.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Transaction not found.");
            s.Response<ProblemDetails>(409, "Transaction isn't awaiting a refund.");
        });
    }

    public override async Task HandleAsync(MarkTransactionRefundedRequest req, CancellationToken ct)
    {
        MarkTransactionRefundedResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
