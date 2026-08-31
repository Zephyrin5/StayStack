using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Promotions.Entities.Configurations;

public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.Property(p => p.DiscountType).HasConversion<string>().HasMaxLength(20).IsRequired();
        // numeric(12,3)/varchar(3), matching every other money-adjacent
        // column (docs/adr/0015) even though DiscountValue stays a plain
        // decimal, not Money - it's a discriminated field (a real currency
        // amount for FixedAmount, a bare percentage for Percentage), so
        // wrapping it would null out the percentage case. But for
        // FixedAmount it IS money, and was the one money value left on
        // numeric(10,2)/character(3) - a KWD fixed-amount promotion of
        // 1.005 would silently truncate to 1.00 on write otherwise.
        builder.Property(p => p.DiscountValue).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(p => p.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(p => p.MaxRedemptions);
        builder.Property(p => p.RedemptionCount).IsRequired();

        // Global uniqueness, not per-host - a guest types a code with no
        // other context to disambiguate which host (or the platform) it
        // belongs to, so a host-scoped and a platform-wide code can't
        // collide, and neither can two different hosts' codes. Code is
        // already normalized to uppercase at creation, so a plain unique
        // index on the stored column is sufficient - no expression index
        // needed. Named explicitly per ADR-0011's gotcha.
        //
        // Partial on status <> 2 (EntityStatus.Archived - Postgres can't see
        // the C# enum name, only the stored int) - "deletion" here is
        // ApplySoftDeleteQueryFilter's soft archive, not a real row removal,
        // so an unfiltered unique index would let an archived promotion
        // permanently reserve its code: CreatePromotionHandler would keep
        // hitting UniqueViolation -> "already in use" for a code nobody can
        // see or redeem any more. Safe to let multiple archived rows share a
        // code - the same soft-delete query filter already makes them
        // invisible to every ordinary lookup (PromotionRedemption's own
        // Code == normalizedCode query included), so nothing ever needs to
        // disambiguate between them. Same partial-index pattern as
        // UnitAvailabilityHold's holder-token index and
        // PromotionRedemption's own promotion+email index.
        builder.HasIndex(p => p.Code, "ix_promotions_code")
            .IsUnique()
            .HasFilter("status <> 2")
            .HasDatabaseName("ix_promotions_code");

        builder.HasIndex(p => p.HostId, "ix_promotions_host_id")
            .HasDatabaseName("ix_promotions_host_id");
    }
}
