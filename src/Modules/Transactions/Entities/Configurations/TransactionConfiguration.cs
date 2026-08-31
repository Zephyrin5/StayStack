using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
using Transactions.Entities;
namespace Transactions.Entities.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);

        builder.ComplexProperty(t => t.Amount, money => money.ConfigureMoney("amount"));
        builder.Property(t => t.RefundAmount).HasColumnType("numeric(12,3)");

        // Stored as text, not the integer enum value - same reasoning as
        // Booking.BookingStatus: legible in psql, safe against the enum's
        // underlying values ever being reordered.
        builder.Property(t => t.TransactionStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(t => t.FailureReason).HasMaxLength(500);

        // Two indexes over the same column, not one reconfigured - see
        // docs/adr/0011 for the naming gotchas that requires.
        builder.HasIndex(t => t.BookingId, "ix_transactions_booking_id");

        // A Pending or Succeeded transaction is the "active" one for a
        // booking - only one may exist at a time. Enforced here, not just
        // in InitiateTransactionHandler's pre-check, which alone can't
        // stop two concurrent requests both passing it and both inserting -
        // see the handler's DbUpdateException catch, which turns a
        // violation of this index into TransactionAlreadyInProgressException.
        builder.HasIndex(t => t.BookingId, "ix_transactions_booking_id_active")
            .IsUnique()
            .HasDatabaseName("ix_transactions_booking_id_active")
            .HasFilter("transaction_status IN ('Pending', 'Succeeded')");
    }
}
