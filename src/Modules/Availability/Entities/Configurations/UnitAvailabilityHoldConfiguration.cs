using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Availability.Entities.Configurations;

public class UnitAvailabilityHoldConfiguration : IEntityTypeConfiguration<UnitAvailabilityHold>
{
    public void Configure(EntityTypeBuilder<UnitAvailabilityHold> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.StayRange).HasColumnType("daterange").IsRequired();
        builder.Property(h => h.Status).HasMaxLength(20).IsRequired();
        builder.ComplexProperty(h => h.TotalPrice, money => money.ConfigureMoney("total_price"));
        builder.Property(h => h.Subtotal).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(h => h.LengthOfStayDiscountAmount).HasColumnType("numeric(12,3)");
        builder.Property(h => h.HolderToken).HasMaxLength(64);

        builder.HasIndex(h => h.UnitId);

        // Backs HoldAvailabilityHandler's per-session active-hold count -
        // partial on 'held' only, matching that query's WHERE clause
        // exactly. Deliberately does NOT include 'booked' - see the
        // query's own comment for why counting a hold that successfully
        // became a booking would be a real (and permanent) customer-facing
        // bug, not just an index-tuning choice. hold_expires_at > @Now is a
        // residual filter applied after this index narrows to the
        // session's own 'held' rows - a non-constant runtime comparison
        // can't be baked into a static partial-index predicate.
        builder.HasIndex(h => h.HolderToken, "ix_unit_availability_holds_holder_token_active")
            .HasFilter("status = 'held'")
            .HasDatabaseName("ix_unit_availability_holds_holder_token_active");

        // Backs Bookings.Jobs.ReconcileOrphanedBookedHoldsJob's query
        // (status = 'booked' AND booked_at <= cutoff) - without this it
        // seq-scans the table every 5 minutes.
        builder.HasIndex(h => new { h.Status, h.BookedAt }, "ix_unit_availability_holds_status_booked_at")
            .HasDatabaseName("ix_unit_availability_holds_status_booked_at");

        // Covers both cleanup queries' predicate shape - the global sweep
        // (ExpiredHoldsSweepJob: status = 'held' AND hold_expires_at <=
        // now(), no unit_id) and, in combination with the UnitId index
        // above, HoldAvailabilityHandler's own per-unit cleanup. Partial on
        // status = 'held' since a 'booked' row is never a cleanup target -
        // matches the exact WHERE clause both DELETEs already use, letting
        // Postgres locate cleanup candidates through this partial index
        // rather than scanning the entire table (the DELETE itself still
        // has to visit the matching heap tuples - this isn't an index-only
        // scan, just an indexed one).
        builder.HasIndex(h => h.HoldExpiresAt)
            .HasFilter("status = 'held'");

        // NOTE: the actual double-booking guard - the exclusion constraint
        // on (unit_id, stay_range) - is NOT configured here, since Npgsql's
        // EF Core provider has no fluent API for EXCLUDE USING gist
        // constraints. See docs/adr/0010 and docs/adr/0011.
    }
}
