using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingManagementTokenConfiguration : IEntityTypeConfiguration<BookingManagementToken>
{
    public void Configure(EntityTypeBuilder<BookingManagementToken> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();

        // At most one token per booking - a second ConfirmBookingHandler
        // call for the same booking can never happen (Booking.Create is
        // called once, per hold, per booking id), but the constraint
        // documents the invariant rather than relying on call-site
        // discipline alone. Named explicitly per ADR-0011's gotcha.
        builder.HasIndex(t => t.BookingId, "ix_booking_management_tokens_booking_id")
            .IsUnique()
            .HasDatabaseName("ix_booking_management_tokens_booking_id");

        // What BookingAccessChecker.ResolveAsync's token path actually
        // queries by - a hash lookup, not a booking id lookup.
        builder.HasIndex(t => t.TokenHash, "ix_booking_management_tokens_token_hash")
            .IsUnique()
            .HasDatabaseName("ix_booking_management_tokens_token_hash");
    }
}
