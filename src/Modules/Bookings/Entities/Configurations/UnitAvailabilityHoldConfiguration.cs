using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Bookings.Entities.Configurations;

public class UnitAvailabilityHoldConfiguration : IEntityTypeConfiguration<UnitAvailabilityHold>
{
    /// <summary>
    ///     No overlapping holds per unit. Raw SQL in a migration, since EF cannot express EXCLUDE USING gist;
    ///     SchemaInvariantsTests pins it (docs/adr/0010).
    /// </summary>
    public const string OverlapExclusionConstraint = "unit_availability_holds_overlap_excl";

    public void Configure(EntityTypeBuilder<UnitAvailabilityHold> builder)
    {
        builder.ToTable("unit_availability_holds", BookingsModel.Schema);

        builder.HasKey(h => h.Id);

        builder.Property(h => h.StayRange).HasColumnType("daterange").IsRequired();
        builder.Property(h => h.Status).HasMaxLength(20).IsRequired();
        builder.ComplexProperty(h => h.TotalPrice, money => money.ConfigureMoney("total_price"));
        builder.Property(h => h.Subtotal).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(h => h.LengthOfStayDiscountAmount).HasColumnType("numeric(12,3)");
        builder.Property(h => h.ClientKey).HasMaxLength(UnitAvailabilityHold.ClientKeyMaxLength);

        builder.HasIndex(h => h.UnitId);

        // Backs the per-client hold cap; the filter must match that query's statuses exactly (docs/adr/0016).
        builder.HasIndex(h => h.ClientKey, "ix_unit_availability_holds_client_key_active")
            .HasFilter($"status IN ('{HoldStatuses.Held}', '{HoldStatuses.PendingPayment}')")
            .HasDatabaseName("ix_unit_availability_holds_client_key_active");

        // Backs the expired-hold cleanup queries.
        builder.HasIndex(h => h.HoldExpiresAt)
            .HasFilter("status = 'held'");
    }
}
