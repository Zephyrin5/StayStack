using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class PendingBookingIntentConfiguration : IEntityTypeConfiguration<PendingBookingIntent>
{
    /// <summary>One intent per hold.</summary>
    public const string HoldIndex = "ix_pending_booking_intents_hold_id";

    public void Configure(EntityTypeBuilder<PendingBookingIntent> builder)
    {
        builder.HasKey(i => i.Id);

        // At most one live intent per hold. ReconcileOrphanedBookingIntentsJob
        // releases the hold behind any intent past its grace period without
        // consulting Bookings, so a second intent for one hold could release it
        // under a live request. The conditional UPDATE in ConfirmHoldAsync
        // decides races for the hold; this index catches the gap in
        // ConfirmBookingHandler's compensation (see its hold_id catch).
        //
        // Plain, not partial - resolving an intent deletes the row, so there
        // is no resolved state left behind to filter out. Named explicitly per
        // ADR-0011's gotcha.
        builder.HasIndex(i => i.HoldId, HoldIndex)
            .IsUnique()
            .HasDatabaseName(HoldIndex);

        // What the reconcile job scans by.
        builder.HasIndex(i => i.CreatedAt, "ix_pending_booking_intents_created_at")
            .HasDatabaseName("ix_pending_booking_intents_created_at");
    }
}
