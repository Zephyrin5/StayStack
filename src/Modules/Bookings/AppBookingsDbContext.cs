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
    public DbSet<PendingBookingIntent> PendingBookingIntents => Set<PendingBookingIntent>();
    public DbSet<CheckoutIdempotencyRecord> CheckoutIdempotencyRecords => Set<CheckoutIdempotencyRecord>();

    // Availability's table, now this module's. A hold is a booking in
    // progress: its lifecycle (held -> pending_payment -> booked) was the
    // booking lifecycle written a second time in a second place and kept in
    // step by hand across a module boundary. Every ordering defect in the
    // last review lived on that seam.
    public DbSet<UnitAvailabilityHold> UnitAvailabilityHolds => Set<UnitAvailabilityHold>();
    // Named per-module, not just "OutboxMessages" - Bookings, Transactions,
    // and Identity all share this same type, and (per docs/adr/0004's own
    // note) every module already shares one physical Postgres schema, so an
    // unqualified name would collide across modules' migrations at the
    // table-name level despite each being a logically separate DbContext.
    public DbSet<OutboxMessage> BookingsOutboxMessages => Set<OutboxMessage>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        // Required by unit_availability_holds' GIST exclusion constraint,
        // which mixes an equality column with a range column (docs/adr/0010).
        // Declared here because this module now owns that table; Availability
        // declared it for the same reason before the merge.
        modelBuilder.HasPostgresExtension("btree_gist");

        modelBuilder.ApplyConfiguration(new BookingConfiguration());
        modelBuilder.ApplyConfiguration(new BookingManagementTokenConfiguration());
        modelBuilder.ApplyConfiguration(new PendingBookingIntentConfiguration());
        modelBuilder.ApplyConfiguration(new CheckoutIdempotencyRecordConfiguration());

        // Mapped only under Npgsql. UnitAvailabilityHold.StayRange is an
        // NpgsqlRange<DateOnly> over a daterange column - the type the GIST
        // exclusion constraint is built on (docs/adr/0010) and not something
        // another provider can express. The unit tests build this context on
        // SQLite, where the model would otherwise fail validation outright,
        // taking every Bookings unit test with it.
        //
        // This did not arise before the merge only because no unit test ever
        // built an Availability context. Same shape as the xmin token on
        // AppTransactionsDbContext: provider-specific mapping belongs on the
        // context, which knows the provider, rather than in a configuration
        // class that does not.
        if (Database.IsNpgsql())
        {
            modelBuilder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
        }
        else
        {
            modelBuilder.Ignore<UnitAvailabilityHold>();
        }
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
