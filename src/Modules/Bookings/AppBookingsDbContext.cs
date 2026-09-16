using Bookings.Entities;
using Bookings.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Bookings;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
public class AppBookingsDbContext(DbContextOptions<AppBookingsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingManagementToken> BookingManagementTokens => Set<BookingManagementToken>();
    public DbSet<CheckoutIdempotencyRecord> CheckoutIdempotencyRecords => Set<CheckoutIdempotencyRecord>();
    public DbSet<RefundObligation> RefundObligations => Set<RefundObligation>();
    public DbSet<UnitAvailabilityHold> UnitAvailabilityHolds => Set<UnitAvailabilityHold>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        // Required by the holds' GIST exclusion constraint (docs/adr/0010).
        modelBuilder.HasPostgresExtension("btree_gist");

        modelBuilder.ApplyConfiguration(new BookingConfiguration());
        modelBuilder.ApplyConfiguration(new BookingManagementTokenConfiguration());
        modelBuilder.ApplyConfiguration(new CheckoutIdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new RefundObligationConfiguration());
        modelBuilder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
    }
}
