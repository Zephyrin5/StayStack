using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.Property(p => p.DiscountType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.DiscountValue).HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(p => p.Currency).HasConversion<string>().HasMaxLength(3).IsFixedLength();
        builder.Property(p => p.MaxRedemptions);
        builder.Property(p => p.RedemptionCount).IsRequired();

        // Global uniqueness, not per-host - a guest types a code with no
        // other context to disambiguate which host (or the platform) it
        // belongs to, so a host-scoped and a platform-wide code can't
        // collide, and neither can two different hosts' codes. Code is
        // already normalized to uppercase at creation, so a plain unique
        // index on the stored column is sufficient - no expression index
        // needed. Named explicitly per ADR-0011's gotcha.
        builder.HasIndex(p => p.Code, "ix_promotions_code")
            .IsUnique()
            .HasDatabaseName("ix_promotions_code");

        builder.HasIndex(p => p.HostId, "ix_promotions_host_id")
            .HasDatabaseName("ix_promotions_host_id");
    }
}
