using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Transactions.Entities;
namespace Transactions.Entities.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Amount).HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(t => t.Currency).HasConversion<string>().HasMaxLength(3).IsFixedLength().IsRequired();

        // Stored as text, not the integer enum value - same reasoning as
        // Booking.BookingStatus: legible in psql, safe against the enum's
        // underlying values ever being reordered.
        builder.Property(t => t.TransactionStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(t => t.FailureReason).HasMaxLength(500);

        // Two indexes over the same column, not one reconfigured - EF Core
        // still treats repeated HasIndex(x => x.Prop).HasDatabaseName(...)
        // calls as the same index even with two different explicit names
        // (confirmed: it generated a migration that DROPped the first one).
        // The HasIndex(expression, name) overload - naming the index at the
        // call itself rather than via a later chained call - is what
        // actually keys two calls on the same property as distinct indexes.
        builder.HasIndex(t => t.BookingId, "ix_transactions_booking_id");

        // A Pending or Succeeded transaction is the "active" one for a
        // booking - only one may exist at a time. Enforced here (not just
        // in InitiateTransactionHandler's pre-check, which alone can't stop
        // two concurrent requests from both passing it and both inserting -
        // see the DbUpdateException catch in the handler that turns a
        // violation of this index into TransactionAlreadyInProgressException).
        // Unlike UnitAvailabilityHold's exclusion constraint, a partial
        // unique index has a real fluent API (HasFilter), so - unlike that
        // one - this belongs in the model, not hand-written into a
        // migration: it now survives a migration squash/regenerate for free.
        builder.HasIndex(t => t.BookingId, "ix_transactions_booking_id_active")
            .IsUnique()
            .HasDatabaseName("ix_transactions_booking_id_active")
            .HasFilter("transaction_status IN ('Pending', 'Succeeded')");
    }
}
