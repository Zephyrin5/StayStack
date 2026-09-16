using Microsoft.EntityFrameworkCore;
using Persistence;
using Transactions.Entities.Configurations;
namespace Transactions;

public sealed class TransactionsModel : IModuleModel
{
    /// <summary>This module's Postgres schema. Its tables and its raw SQL name it (docs/adr/0004).</summary>
    public const string Schema = "transactions";

    public void Configure(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new TransactionConfiguration());
    }
}
