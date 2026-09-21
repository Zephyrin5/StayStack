using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingManagementTokenConfiguration : IEntityTypeConfiguration<BookingManagementToken>
{
    /// <summary>
    ///     How many tokens one booking may have at once. Enforced where a token is minted, not by
    ///     the schema: a constraint here could only refuse the insert, and refusing a legitimate
    ///     retry is the failure the cap exists to avoid (docs/adr/0022, ManagementTokenCapTests).
    /// </summary>
    public const int MaxLivePerBooking = 5;

    public void Configure(EntityTypeBuilder<BookingManagementToken> builder)
    {
        builder.ToTable("booking_management_tokens", BookingsModel.Schema);

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();

        // Not unique: several tokens per booking are legitimate, and ManagementTokenCapTests
        // holds how many and which of them survive (docs/adr/0022). Also serves the eviction's
        // ordered read. Named explicitly per ADR-0011's gotcha.
        builder.HasIndex(t => t.BookingId, "ix_booking_management_tokens_booking_id")
            .HasDatabaseName("ix_booking_management_tokens_booking_id");

        // What BookingAccessChecker.ResolveAsync's token path actually
        // queries by - a hash lookup, not a booking id lookup.
        builder.HasIndex(t => t.TokenHash, "ix_booking_management_tokens_token_hash")
            .IsUnique()
            .HasDatabaseName("ix_booking_management_tokens_token_hash");
    }
}
