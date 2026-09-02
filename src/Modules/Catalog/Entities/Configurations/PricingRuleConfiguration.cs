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

        // "At most one active length-of-stay rule per unit" - the invariant
        // PricingRuleOverlapChecker.EnsureNoLengthOfStayConflict enforces in
        // application code, now also held by the database.
        //
        // PricingCalculator reads these with FirstOrDefault over an unordered
        // ToListAsync result, so "at most one match" is not a nicety - it is
        // what makes the read deterministic. A second active rule would not
        // throw anywhere; it would silently make the price depend on row
        // order. That is a data invariant, so it belongs in the schema rather
        // than resting on every writer remembering to call the checker.
        //
        // Partial on status <> 2 (EntityStatus.Archived - Postgres only sees
        // the stored int), same pattern as ix_promotions_code: an archived
        // rule must not block creating its replacement.
        //
        // rule_type is compared as text because it is stored via
        // HasConversion<string>(), unlike status.
        builder.HasIndex(r => r.UnitId, "ix_pricing_rules_unit_length_of_stay_active")
            .IsUnique()
            .HasFilter("rule_type = 'LengthOfStayDiscount' AND status <> 2")
            .HasDatabaseName("ix_pricing_rules_unit_length_of_stay_active");
    }
}
