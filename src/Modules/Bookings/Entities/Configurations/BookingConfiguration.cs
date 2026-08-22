using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Bookings.Entities.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.GuestName).HasMaxLength(200).IsRequired();
        builder.Property(b => b.GuestEmail).HasMaxLength(200).IsRequired();
        builder.Property(b => b.GuestPhone).HasMaxLength(50);
        builder.Property(b => b.Currency).HasMaxLength(3).IsRequired();

        // Stored as text, not the integer enum value - same reasoning as
        // Property.PropertyType (Catalog): legible in psql, safe against
        // the enum's underlying values ever being reordered.
        builder.Property(b => b.BookingStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(b => b.UnitId);
        builder.HasIndex(b => b.HoldId).IsUnique();
        builder.HasIndex(b => b.CustomerId);
    }
}
