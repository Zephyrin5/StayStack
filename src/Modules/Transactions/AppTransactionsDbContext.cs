using Microsoft.EntityFrameworkCore;
using Persistence;
using Transactions.Entities;
using Transactions.Entities.Configurations;
namespace Transactions;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
public class AppTransactionsDbContext(DbContextOptions<AppTransactionsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TransactionConfiguration());
    }
}
