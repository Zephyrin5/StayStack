using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using FastEndpoints;
using Mediator;
using Transactions.Features.GetTransactions;

namespace Api.Endpoints.Transactions;

// Administrator-gated for the same reason as MarkTransactionSucceeded/Failed -
// this is the admin-facing view used to find transactions to act on, not
// something a caller should be able to browse for other people's bookings.
public class GetTransactionsEndpoint(IMediator mediator) : Endpoint<GetTransactionsRequest, PagedResponse<TransactionSummary>>
{
    public override void Configure()
    {
        Get("");
        Policies(AuthorizationPolicies.Administrator);
        Group<TransactionsGroup>();

        Summary(s =>
        {
            s.Summary = "List transactions, optionally filtered by status (admin-only)";
            s.Description = "Paginated - defaults to page 1, 20 per page.";
            s.Response<PagedResponse<TransactionSummary>>(200, "Transactions returned.");
        });
    }

    public override async Task HandleAsync(GetTransactionsRequest req, CancellationToken ct)
    {
        PagedResponse<TransactionSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
