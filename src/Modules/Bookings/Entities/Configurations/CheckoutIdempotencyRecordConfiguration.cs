using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class CheckoutIdempotencyRecordConfiguration : IEntityTypeConfiguration<CheckoutIdempotencyRecord>
{
    /// <summary>One record per idempotency key.</summary>
    public const string KeyHashIndex = "ix_checkout_idempotency_key_hash";

    public void Configure(EntityTypeBuilder<CheckoutIdempotencyRecord> builder)
    {
        builder.ToTable("checkout_idempotency_records");

        builder.HasKey(r => r.BookingId);

        // At most one record per key, and this is the concurrency control
        // rather than a lookup optimisation. Two simultaneous requests carrying
        // the same key both miss the replay read - it happens before either has
        // written anything. For the same hold, the hold transition's row lock
        // separates them. For different holds, this insert fails for the loser,
        // and its atomic scope rolls its hold transition back with it, so one
        // key never commits two bookings.
        //
        // Plain, not partial: a record is only ever written with its booking,
        // so there is no unresolved state to filter out. Named explicitly per
        // ADR-0011's gotcha.
        builder.HasIndex(r => r.KeyHash, KeyHashIndex)
            .IsUnique()
            .HasDatabaseName(KeyHashIndex);

        // What the purge job scans by.
        builder.HasIndex(r => r.CreatedAt, "ix_checkout_idempotency_created_at")
            .HasDatabaseName("ix_checkout_idempotency_created_at");

        // Both are Base64 SHA-256: 44 characters including the padding byte.
        // Bounded rather than unbounded `text` because nothing else can ever
        // be written here - the values are computed, never client-supplied,
        // and a length that stops matching is a bug worth failing on.
        builder.Property(r => r.KeyHash).IsRequired().HasMaxLength(44);
        builder.Property(r => r.RequestFingerprint).IsRequired().HasMaxLength(44);
    }
}
