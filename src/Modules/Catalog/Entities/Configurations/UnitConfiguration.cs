using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Persistence;
namespace Catalog.Entities.Configurations;

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.HasKey(u => u.Id);

        builder.ComplexProperty(u => u.BasePrice, money => money.ConfigureMoney("base_price"));

        builder.Property(u => u.Name)
            .IsRequired();

        builder.HasIndex(u => u.PropertyId);
    }
}
