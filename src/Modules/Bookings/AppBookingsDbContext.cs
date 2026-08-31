using Bookings.Entities;
using Bookings.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Outbox;
using Persistence;
namespace Bookings;

public class AppBookingsDbContext(DbContextOptions<AppBookingsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingManagementToken> BookingManagementTokens => Set<BookingManagementToken>();
    // Named per-module, not just "OutboxMessages" - Bookings, Transactions,
    // and Identity all share this same type, and (per docs/adr/0004's own
    // note) every module already shares one physical Postgres schema, so an
    // unqualified name would collide across modules' migrations at the
    // table-name level despite each being a logically separate DbContext.
    public DbSet<OutboxMessage> BookingsOutboxMessages => Set<OutboxMessage>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new BookingConfiguration());
        modelBuilder.ApplyConfiguration(new BookingManagementTokenConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
