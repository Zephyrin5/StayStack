using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.City).HasMaxLength(100);

        // Stored as text, not the integer enum value - a lot more legible
        // when you're staring at rows in psql, and safe against the enum's
        // underlying values ever being reordered.
        builder.Property(p => p.PropertyType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(p => p.Name)
            .IsRequired();

        builder.HasIndex(p => p.HostId);
    }
}
