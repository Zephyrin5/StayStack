using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.BasePrice).HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(u => u.Currency).HasConversion<string>().HasMaxLength(3).IsFixedLength().IsRequired();

        builder.Property(u => u.Name)
            .IsRequired();

        builder.HasIndex(u => u.PropertyId);
    }
}
