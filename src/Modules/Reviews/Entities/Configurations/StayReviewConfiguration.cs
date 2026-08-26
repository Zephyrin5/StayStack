using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Reviews.Entities.Configurations;

public class StayReviewConfiguration : IEntityTypeConfiguration<StayReview>
{
    public void Configure(EntityTypeBuilder<StayReview> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReviewerGuestEmail).HasMaxLength(320).IsRequired();
        builder.Property(r => r.OverallRating).HasColumnType("numeric(3,2)").IsRequired();
        builder.Property(r => r.HostReplyText);

        // One review per stay - the actual guarantee "already reviewed
        // this booking" relies on, not an app-level check-then-insert.
        // Named explicitly per ADR-0011's gotcha.
        builder.HasIndex(r => r.BookingId, "ix_stay_reviews_booking_id")
            .IsUnique()
            .HasDatabaseName("ix_stay_reviews_booking_id");

        // What GetPropertyReviewsHandler filters/aggregates by.
        builder.HasIndex(r => r.PropertyId, "ix_stay_reviews_property_id")
            .HasDatabaseName("ix_stay_reviews_property_id");

        // What GetHostStayReviewsHandler filters by.
        builder.HasIndex(r => r.HostId, "ix_stay_reviews_host_id")
            .HasDatabaseName("ix_stay_reviews_host_id");
    }
}
