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

        builder.HasIndex(t => t.BookingId);
    }
}
