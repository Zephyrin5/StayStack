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

        // Every transition on Transaction guards its starting state -
        // MarkSucceeded and MarkFailed require Pending, the refund trio
        // requires Succeeded - and each of those guards reads an in-memory
        // copy. Two callers that load the same Pending row both pass their
        // own check, both write, and the second silently overwrites the
        // first: a transaction both Succeeded and Failed depending on who
        // committed last.
        //
        // xmin rather than a row lock in the handlers. The entity calls
        // itself a one-shot ledger entry, and that is a property of the row,
        // not of the two call sites that happen to exist today - the refund
        // sub-lifecycle races identically and would have needed the same
        // guard bolted on separately. EF puts the token in the WHERE clause
        // of every UPDATE, so a stale write matches zero rows and raises
        // DbUpdateConcurrencyException instead of landing.
        //
        // Conditional on the provider, and it has to be: xmin is a Postgres
        // *system* column, free to map because it already exists on every
        // table. Under SQLite - which the unit tests use - EF has no such
        // column to bind to and creates a real NOT NULL one instead, so
        // every insert fails on it. Configuring it here rather than in
        // TransactionConfiguration is what makes the provider knowable.
        if (Database.IsNpgsql())
        {
            modelBuilder.Entity<Transaction>()
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .IsRowVersion()
                .ValueGeneratedOnAddOrUpdate();
        }
    }
}
