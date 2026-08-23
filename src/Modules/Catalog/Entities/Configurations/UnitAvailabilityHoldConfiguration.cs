using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class UnitAvailabilityHoldConfiguration : IEntityTypeConfiguration<UnitAvailabilityHold>
{
    public void Configure(EntityTypeBuilder<UnitAvailabilityHold> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.StayRange).HasColumnType("daterange").IsRequired();
        builder.Property(h => h.Status).HasMaxLength(20).IsRequired();
        builder.Property(h => h.TotalPrice).HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(h => h.Currency).HasConversion<string>().HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasIndex(h => h.UnitId);

        // NOTE: the actual double-booking guard - the exclusion constraint
        // on (unit_id, stay_range) - is NOT configured here. Confirmed by
        // checking current Npgsql EF Core provider docs: there is no
        // fluent API for EXCLUDE USING gist constraints, only an open
        // feature request. It's added via migrationBuilder.Sql(...) in the
        // Initial migration's Up() method instead.
    }
}
