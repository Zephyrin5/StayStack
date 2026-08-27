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

        // GetPropertiesHandler's city filter is ILIKE '%term%' - freeform,
        // case-insensitive, leading-wildcard - which a plain B-tree index
        // can never serve. gin_trgm_ops (pg_trgm, enabled in
        // AppCatalogDbContext.OnStayStackModelCreating) is the standard
        // Postgres way to index substring search instead of falling back to
        // a full table scan on every property search.
        // Named explicitly per ADR-0011's gotcha - HasDatabaseName is
        // required too, or the snake_case naming convention overrides the
        // name pinned at the HasIndex call itself.
        builder.HasIndex(p => p.City, "ix_properties_city_trgm")
            .HasDatabaseName("ix_properties_city_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
