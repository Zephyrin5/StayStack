using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class CheckoutIdempotencyRecordConfiguration : IEntityTypeConfiguration<CheckoutIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<CheckoutIdempotencyRecord> builder)
    {
        builder.HasKey(r => r.BookingId);

        // At most one record per key, and this is the concurrency control
        // rather than a lookup optimisation. Two simultaneous requests
        // carrying the same key both miss the replay read - it happens before
        // either has written anything - so what separates them is this insert
        // failing for the loser inside ConfirmBookingHandler's first
        // transaction, before the hold is transitioned. Without it both would
        // proceed and the second would fail confusingly on the consumed hold,
        // reporting 404 for a checkout that had in fact succeeded.
        //
        // Plain, not partial: an unresolved record is deleted rather than
        // flagged, so there is no resolved state to filter out. Named
        // explicitly per ADR-0011's gotcha.
        builder.HasIndex(r => r.KeyHash, "ix_checkout_idempotency_key_hash")
            .IsUnique()
            .HasDatabaseName("ix_checkout_idempotency_key_hash");

        // What the purge job scans by.
        builder.HasIndex(r => r.CompletedAt, "ix_checkout_idempotency_completed_at")
            .HasDatabaseName("ix_checkout_idempotency_completed_at");

        // Both are Base64 SHA-256: 44 characters including the padding byte.
        // Bounded rather than unbounded `text` because nothing else can ever
        // be written here - the values are computed, never client-supplied,
        // and a length that stops matching is a bug worth failing on.
        builder.Property(r => r.KeyHash).IsRequired().HasMaxLength(44);
        builder.Property(r => r.RequestFingerprint).IsRequired().HasMaxLength(44);

        // Base64Url of 64 random bytes - 86 characters, no padding. See
        // SecureToken.Generate.
        builder.Property(r => r.ManagementToken).HasMaxLength(86);
    }
}
