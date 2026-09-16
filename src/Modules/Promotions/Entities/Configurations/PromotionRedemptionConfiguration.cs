using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Promotions.Entities.Configurations;

public class PromotionRedemptionConfiguration : IEntityTypeConfiguration<PromotionRedemption>
{
    /// <summary>One unreversed redemption per promotion and guest email.</summary>
    public const string PromotionEmailIndex = "ix_promotion_redemptions_promotion_email";

    public void Configure(EntityTypeBuilder<PromotionRedemption> builder)
    {
        builder.ToTable("promotion_redemptions", PromotionsModel.Schema);

        builder.HasKey(r => r.Id);

        builder.Property(r => r.GuestEmail).HasMaxLength(320).IsRequired();
        builder.ComplexProperty(r => r.DiscountAmount, money => money.ConfigureMoney("discount_amount"));

        // The one-redemption-per-guest-per-code enforcement - a plain unique
        // index, since this invariant has no range-overlap shape. Named
        // explicitly per ADR-0011's gotcha. Partial on ReversedAt IS NULL: a
        // reversed (cancelled) redemption does not block the same email from
        // redeeming the same code again, while the row itself survives as
        // history (see ReversedAt's own doc comment).
        builder.HasIndex(r => new { r.PromotionId, r.GuestEmail }, PromotionEmailIndex)
            .IsUnique()
            .HasFilter("reversed_at IS NULL")
            .HasDatabaseName(PromotionEmailIndex);

        builder.HasIndex(r => r.BookingId, "ix_promotion_redemptions_booking_id")
            .HasDatabaseName("ix_promotion_redemptions_booking_id");
    }
}
