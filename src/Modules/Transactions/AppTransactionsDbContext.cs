using Microsoft.EntityFrameworkCore;
using Outbox;
using Persistence;
using Transactions.Entities;
using Transactions.Entities.Configurations;
namespace Transactions;

public class AppTransactionsDbContext(DbContextOptions<AppTransactionsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    // See AppBookingsDbContext.BookingsOutboxMessages for why this is
    // module-prefixed rather than just "OutboxMessages".
    public DbSet<OutboxMessage> TransactionsOutboxMessages => Set<OutboxMessage>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TransactionConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
