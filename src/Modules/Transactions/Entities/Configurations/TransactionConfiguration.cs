using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
using Transactions.Entities;
namespace Transactions.Entities.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    /// <summary>At most one Pending or Succeeded transaction per booking.</summary>
    public const string ActiveTransactionIndex = "ix_transactions_booking_id_active";

    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);

        // Every transition guards its starting state in memory; xmin makes a stale write match no row.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion()
            .ValueGeneratedOnAddOrUpdate();

        builder.ComplexProperty(t => t.Amount, money => money.ConfigureMoney("amount"));

        // The currency is amount_currency, paired back on read; see Transaction.RefundAmount.
        builder.Property<decimal?>(Transaction.RefundAmountField)
            .HasColumnName("refund_amount")
            .HasColumnType("numeric(12,3)");

        // Enums as text: legible in psql and safe against reordering.
        builder.Property(t => t.TransactionStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.FailureReason).HasMaxLength(500);
        builder.Property(t => t.RefundCause).HasConversion<string>().HasMaxLength(20);

        // Two indexes on one column need explicit names (docs/adr/0011).
        builder.HasIndex(t => t.BookingId, "ix_transactions_booking_id");

        // The authority behind InitiateTransactionHandler's early check.
        builder.HasIndex(t => t.BookingId, ActiveTransactionIndex)
            .IsUnique()
            .HasDatabaseName(ActiveTransactionIndex)
            .HasFilter("transaction_status IN ('Pending', 'Succeeded')");
    }
}
