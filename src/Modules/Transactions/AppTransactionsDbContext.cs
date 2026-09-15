using Microsoft.EntityFrameworkCore;
using Persistence;
using Transactions.Entities;
using Transactions.Entities.Configurations;
namespace Transactions;

public class AppTransactionsDbContext(DbContextOptions<AppTransactionsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TransactionConfiguration());
    }
}
