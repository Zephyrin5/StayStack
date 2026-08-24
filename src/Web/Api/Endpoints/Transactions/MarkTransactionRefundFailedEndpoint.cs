using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Transactions.Features.MarkTransactionRefundFailed;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Transactions;

// Same Administrator stand-in reasoning as MarkTransactionSucceededEndpoint.
public class MarkTransactionRefundFailedEndpoint(IMediator mediator)
    : Endpoint<MarkTransactionRefundFailedRequest, MarkTransactionRefundFailedResponse>
{
    public override void Configure()
    {
        Post("{TransactionId}/refund-fail");
        Policies(AuthorizationPolicies.Administrator);
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "Mark a refund failed (admin-only stand-in for a gateway webhook)";
            s.Description = "Only valid from RefundPending. The booking is left as-is - it's already " +
                            "Cancelled regardless of whether the refund itself succeeded.";
            s.Response<MarkTransactionRefundFailedResponse>(200, "Refund marked failed.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Transaction not found.");
            s.Response<ProblemDetails>(409, "Transaction isn't awaiting a refund.");
        });
    }

    public override async Task HandleAsync(MarkTransactionRefundFailedRequest req, CancellationToken ct)
    {
        MarkTransactionRefundFailedResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
