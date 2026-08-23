using FastEndpoints;
namespace Api.Endpoints.Transactions;

public sealed class TransactionsGroup : Group
{
    public TransactionsGroup()
    {
        Configure("api/transactions", ep => { ep.Description(b => b.WithTags("Transactions")); });
    }
}
