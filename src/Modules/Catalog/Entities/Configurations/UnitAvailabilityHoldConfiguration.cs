using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class UnitAvailabilityHoldConfiguration : IEntityTypeConfiguration<UnitAvailabilityHold>
{
    public void Configure(EntityTypeBuilder<UnitAvailabilityHold> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.StayRange).HasColumnType("daterange").IsRequired();
        builder.Property(h => h.Status).HasMaxLength(20).IsRequired();
        builder.Property(h => h.TotalPrice).HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(h => h.Currency).HasConversion<string>().HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasIndex(h => h.UnitId);

        // Covers both cleanup queries' predicate shape - the global sweep
        // (ExpiredHoldsSweepJob: status = 'held' AND hold_expires_at <=
        // now(), no unit_id) and, in combination with the UnitId index
        // above, HoldAvailabilityHandler's own per-unit cleanup. Partial on
        // status = 'held' since a 'booked' row is never a cleanup target -
        // matches the exact WHERE clause both DELETEs already use, so
        // Postgres can satisfy either with an index-only range scan
        // instead of a growing full-table scan.
        builder.HasIndex(h => h.HoldExpiresAt)
            .HasFilter("status = 'held'");

        // NOTE: the actual double-booking guard - the exclusion constraint
        // on (unit_id, stay_range) - is NOT configured here, since Npgsql's
        // EF Core provider has no fluent API for EXCLUDE USING gist
        // constraints. See docs/adr/0010 and docs/adr/0011.
    }
}
