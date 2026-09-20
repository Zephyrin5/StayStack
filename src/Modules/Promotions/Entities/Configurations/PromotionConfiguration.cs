using Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Promotions.Entities.Configurations;

public class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    /// <summary>One active promotion per code.</summary>
    public const string CodeIndex = "ix_promotions_code";

    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("promotions", PromotionsModel.Schema);
        builder.HasSoftDeleteFilter();

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Code).HasMaxLength(30).IsRequired();
        builder.Property(p => p.DiscountType).HasConversion<string>().HasMaxLength(20).IsRequired();
        // numeric(12,3), matching every other money-adjacent column
        // (docs/adr/0015), even though DiscountValue stays a plain decimal,
        // not Money - it's discriminated (a currency amount for
        // FixedAmount, a bare percentage for Percentage), so wrapping it in
        // Money would null out the percentage case. But for FixedAmount it
        // IS money: at numeric(10,2), a KWD fixed-amount promotion of
        // 1.005 would silently truncate to 1.00.
        builder.Property(p => p.DiscountValue).HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(p => p.Currency).HasConversion<string>().HasMaxLength(3);
        builder.Property(p => p.MaxRedemptions);
        builder.Property(p => p.RedemptionCount).IsRequired();

        // Global uniqueness, not per-host - a guest types a code with no
        // other context to disambiguate which host (or the platform) it
        // belongs to. Code is already normalized to uppercase at creation,
        // so a plain unique index suffices. Named explicitly per ADR-0011's
        // gotcha.
        //
        // Partial on the soft-delete predicate - an unfiltered unique index would let an
        // archived promotion permanently reserve its code:
        // CreatePromotionHandler would keep hitting UniqueViolation for a
        // code nobody can see or redeem. Safe to let multiple archived rows
        // share a code, since the soft-delete query filter already makes them
        // invisible to every ordinary lookup.
        builder.HasIndex(p => p.Code, CodeIndex)
            .IsUnique()
            .HasFilter(SoftDelete.NotArchived)
            .HasDatabaseName(CodeIndex);

        builder.HasIndex(p => p.HostId, "ix_promotions_host_id")
            .HasDatabaseName("ix_promotions_host_id");
    }
}
