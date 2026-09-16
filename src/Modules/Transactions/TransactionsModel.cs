using Microsoft.EntityFrameworkCore;
using Persistence;
using Transactions.Entities.Configurations;
namespace Transactions;

public sealed class TransactionsModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new TransactionConfiguration());
    }
}
