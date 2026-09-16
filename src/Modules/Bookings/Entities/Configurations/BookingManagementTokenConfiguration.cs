using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingManagementTokenConfiguration : IEntityTypeConfiguration<BookingManagementToken>
{
    public void Configure(EntityTypeBuilder<BookingManagementToken> builder)
    {
        builder.ToTable("booking_management_tokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();

        // Not unique: several tokens per booking are legitimate. Replaying a
        // checkout mints a fresh token, since only hashes are stored, and the
        // earlier one stays valid so a guest who did receive the first response
        // is not locked out by their own client's retry. All tokens name one
        // booking and expire on its clock. The index narrows
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
