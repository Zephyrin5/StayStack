using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    public void Configure(EntityTypeBuilder<PricingRule> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.RuleType).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(r => r.DateRange).HasColumnType("daterange");
        builder.Property(r => r.OverridePrice).HasColumnType("numeric(10,2)");

        builder.Property(r => r.DaysOfWeek).HasColumnType("integer[]");
        builder.Property(r => r.Multiplier).HasColumnType("numeric(5,3)");

        builder.Property(r => r.MinNights);
        builder.Property(r => r.DiscountPercent).HasColumnType("numeric(5,2)");

        // Every read path (PricingCalculator's callers, the write-time
        // overlap check) filters by (UnitId, RuleType) first. Named
        // explicitly at the call site per ADR-0011's gotcha - the
        // snake_case naming convention can silently rename an index whose
        // name was only ever set via a trailing HasDatabaseName(...).
        builder.HasIndex(r => new { r.UnitId, r.RuleType }, "ix_pricing_rules_unit_type")
            .HasDatabaseName("ix_pricing_rules_unit_type");
    }
}
