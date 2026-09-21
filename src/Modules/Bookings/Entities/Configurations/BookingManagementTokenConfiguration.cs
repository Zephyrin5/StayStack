using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingManagementTokenConfiguration : IEntityTypeConfiguration<BookingManagementToken>
{
    /// <summary>
    ///     How many tokens one booking may have at once. A replay mints a token rather than
    ///     returning the original, whose plaintext is not stored, so without a cap the count grows
    ///     with the number of replays and every one of them stays live for the booking's whole
    ///     window. Enforced where a token is minted, not by the schema - a constraint here could
    ///     only refuse the insert, and refusing a legitimate retry is the failure this exists to
    ///     avoid (docs/adr/0022).
    /// </summary>
    public const int MaxLivePerBooking = 5;

    public void Configure(EntityTypeBuilder<BookingManagementToken> builder)
    {
        builder.ToTable("booking_management_tokens", BookingsModel.Schema);

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();

        // Not unique: up to MaxLivePerBooking tokens per booking are legitimate. Replaying a
        // checkout mints a fresh token, since only hashes are stored, and the earlier ones stay
        // valid so a guest who did receive the first response is not locked out by their own
        // client's retry. All tokens name one booking and expire on its clock. This index also
        // serves the eviction's ordered read, which never sees more rows than the cap. Named
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
