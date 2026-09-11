using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingManagementTokenConfiguration : IEntityTypeConfiguration<BookingManagementToken>
{
    public void Configure(EntityTypeBuilder<BookingManagementToken> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();

        // Several tokens per booking are legitimate, so this is no longer
        // unique.
        //
        // It was, and the reasoning held at the time: a second
        // ConfirmBookingHandler call for one booking could not happen, since
        // Booking.Create runs once per hold per booking id. Replaying a
        // checkout is that second issuance. It mints a fresh token rather than
        // returning a stored one, because storing the plaintext is what let a
        // single table read produce working credentials for every guest
        // checkout in the replay window.
        //
        // Deliberately additive rather than a rotation: a guest who did
        // receive the first response must not be locked out by their own
        // client's retry, and the tokens are equivalent in power and scope -
        // all of them name one booking and expire on that booking's own
        // clock. Kept as a plain index because it still narrows
        // BookingAccessChecker's (booking_id, token_hash) predicate. Named
        // explicitly per ADR-0011's gotcha.
        builder.HasIndex(t => t.BookingId, "ix_booking_management_tokens_booking_id")
            .HasDatabaseName("ix_booking_management_tokens_booking_id");

        // What BookingAccessChecker.ResolveAsync's token path actually
        // queries by - a hash lookup, not a booking id lookup.
        builder.HasIndex(t => t.TokenHash, "ix_booking_management_tokens_token_hash")
            .IsUnique()
            .HasDatabaseName("ix_booking_management_tokens_token_hash");
    }
}
