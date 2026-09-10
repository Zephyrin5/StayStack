using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Bookings.Entities.Configurations;

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
        builder.Property(h => h.ClientKey).HasMaxLength(UnitAvailabilityHold.ClientKeyMaxLength);

        builder.HasIndex(h => h.UnitId);

        // Backs HoldAvailabilityHandler's concurrent-hold cap - partial on
        // the two statuses that query counts, matching its WHERE clause
        // exactly. A filter narrower than the query silently stops covering
        // it, and the cap runs inside a Serializable transaction on the hold
        // path, so a sequential scan there is not merely slower.
        // Deliberately excludes 'booked' - see that query's own comment for
        // why counting a successfully-booked hold would be a permanent
        // customer-facing bug, not just an index-tuning choice.
        // hold_expires_at > @Now is a residual filter applied after this
        // index narrows to the client's rows - a runtime comparison can't be
        // baked into a static partial-index predicate.
        //
        // Keyed on client_key. The cap used to be keyed on a hold-session
        // cookie, which meant the caller chose their own budget by dropping
        // it (docs/adr/0016); that cookie and its holder_token column have
        // since been removed entirely, since nothing ever read them back.
        builder.HasIndex(h => h.ClientKey, "ix_unit_availability_holds_client_key_active")
            .HasFilter($"status IN ('{HoldStatuses.Held}', '{HoldStatuses.PendingPayment}')")
            .HasDatabaseName("ix_unit_availability_holds_client_key_active");

        // No (status, booked_at) index any more. It existed solely for
        // ReconcileOrphanedBookedHoldsJob's candidate query, which
        // docs/adr/0017 replaced with a scan of Bookings' own
        // pending_booking_intents - nothing queries booked_at now, so the
        // index was pure write-side cost.

        // Covers both cleanup queries' predicate shape - the global sweep
        // (ExpiredHoldsSweepJob: status = 'held' AND hold_expires_at <=
        // now(), no unit_id) and, combined with the UnitId index above,
        // HoldAvailabilityHandler's per-unit cleanup. Partial on
        // status = 'held' since a 'booked' row is never a cleanup target,
        // letting Postgres locate candidates through this index instead of
        // scanning the whole table (the DELETE still visits the matching
        // heap tuples - this isn't index-only).
        builder.HasIndex(h => h.HoldExpiresAt)
            .HasFilter("status = 'held'");

        // NOTE: the actual double-booking guard - the exclusion constraint
        // on (unit_id, stay_range) - is NOT configured here, since Npgsql's
        // EF Core provider has no fluent API for EXCLUDE USING gist
        // constraints. See docs/adr/0010 and docs/adr/0011.
    }
}
