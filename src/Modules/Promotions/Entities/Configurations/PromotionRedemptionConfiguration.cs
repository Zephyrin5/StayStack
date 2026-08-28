using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Promotions.Entities.Configurations;

public class PromotionRedemptionConfiguration : IEntityTypeConfiguration<PromotionRedemption>
{
    public void Configure(EntityTypeBuilder<PromotionRedemption> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.GuestEmail).HasMaxLength(320).IsRequired();
        builder.ComplexProperty(r => r.DiscountAmount, money => money.ConfigureMoney("discount_amount"));

        // The actual one-redemption-per-guest-per-code enforcement - a
        // plain Postgres unique index, not a GIST exclusion constraint like
        // UnitAvailabilityHold's double-booking guard, since this invariant
        // has no range-overlap shape (see docs/adr/0010's own reasoning for
        // when exclusion constraints are the right tool vs. not). Named
        // explicitly per ADR-0011's gotcha. Partial on ReversedAt IS NULL -
        // a reversed (cancelled) redemption no longer blocks the same email
        // from redeeming the same code again, while the row itself survives
        // as history (see ReversedAt's own doc comment) - same partial-
        // index pattern as UnitAvailabilityHold's holder-token index.
        builder.HasIndex(r => new { r.PromotionId, r.GuestEmail }, "ix_promotion_redemptions_promotion_email")
            .IsUnique()
            .HasFilter("reversed_at IS NULL")
            .HasDatabaseName("ix_promotion_redemptions_promotion_email");

        builder.HasIndex(r => r.BookingId, "ix_promotion_redemptions_booking_id")
            .HasDatabaseName("ix_promotion_redemptions_booking_id");
    }
}
