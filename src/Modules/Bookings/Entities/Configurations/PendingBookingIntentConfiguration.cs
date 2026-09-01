using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class PendingBookingIntentConfiguration : IEntityTypeConfiguration<PendingBookingIntent>
{
    public void Configure(EntityTypeBuilder<PendingBookingIntent> builder)
    {
        builder.HasKey(i => i.Id);

        // At most one live intent per hold, and this is load-bearing rather
        // than documentary: ReconcileOrphanedBookingIntentsJob no longer joins
        // against Bookings to decide whether a hold is genuinely orphaned, so
        // a second intent row for the same hold (left by a retry against a
        // hold an earlier crashed attempt already consumed) would make the job
        // release a hold out from under a live request. Unique here means that
        // second insert fails before ConfirmHoldAsync is ever called.
        //
        // Plain, not partial - resolving an intent deletes the row, so there
        // is no resolved state left behind to filter out. Named explicitly per
        // ADR-0011's gotcha.
        builder.HasIndex(i => i.HoldId, "ix_pending_booking_intents_hold_id")
            .IsUnique()
            .HasDatabaseName("ix_pending_booking_intents_hold_id");

        // What the reconcile job scans by.
        builder.HasIndex(i => i.CreatedAt, "ix_pending_booking_intents_created_at")
            .HasDatabaseName("ix_pending_booking_intents_created_at");
    }
}
