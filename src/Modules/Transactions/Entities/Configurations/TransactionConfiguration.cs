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

        // Every transition on this entity guards its starting state -
        // MarkSucceeded and MarkFailed require Pending, the refund trio
        // requires Succeeded - and each of those guards reads an in-memory
        // copy. Two callers that load the same Pending row both pass their
        // own check, both write, and the second silently overwrites the
        // first: a transaction both Succeeded and Failed depending on who
        // committed last.
        //
        // xmin rather than a row lock in the handlers. The entity's own
        // comment calls it a one-shot ledger entry, and that is a property
        // of the row, not of the two call sites that happen to exist today -
        // the refund sub-lifecycle races identically and would have needed
        // the same treatment bolted on separately. As a system column it
        // costs no schema change and no extra read: EF adds it to the WHERE
        // clause of every UPDATE, so a stale write affects zero rows and
        // raises DbUpdateConcurrencyException instead of landing.
        // Spelled as the shadow system column rather than the older
        // UseXminAsConcurrencyToken() helper, which this Npgsql version no
        // longer exposes.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion()
            .ValueGeneratedOnAddOrUpdate();

        builder.ComplexProperty(t => t.Amount, money => money.ConfigureMoney("amount"));
        // Mapped by backing-field name, not by the Money?-typed RefundAmount
        // property: the currency lives on amount_currency and is paired back
        // on read, so this stays exactly the one column it has always been -
        // a type-only change with no migration. See Transaction.RefundAmount.
        builder.Property<decimal?>(Transaction.RefundAmountField)
            .HasColumnName("refund_amount")
            .HasColumnType("numeric(12,3)");

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
