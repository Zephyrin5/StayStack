using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Reviews.Entities.Configurations;

public class GuestReviewConfiguration : IEntityTypeConfiguration<GuestReview>
{
    /// <summary>One guest review per booking.</summary>
    public const string BookingIndex = "ix_guest_reviews_booking_id";

    public void Configure(EntityTypeBuilder<GuestReview> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.GuestEmail).HasMaxLength(320).IsRequired();

        // One guest-review per booking - same reasoning as
        // StayReview's own BookingId uniqueness.
        builder.HasIndex(r => r.BookingId, BookingIndex)
            .IsUnique()
            .HasDatabaseName(BookingIndex);

        builder.HasIndex(r => r.HostId, "ix_guest_reviews_host_id")
            .HasDatabaseName("ix_guest_reviews_host_id");
    }
}
