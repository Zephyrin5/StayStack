using Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    /// <summary>One active length-of-stay discount per unit per threshold.</summary>
    public const string LengthOfStayTierIndex = "ix_pricing_rules_unit_min_nights_active";

    /// <summary>
    ///     No overlapping active date-range overrides per unit. Created by raw SQL in
    ///     the AddPricingRuleOverlapConstraints migration, which EF cannot express.
    /// </summary>
    public const string DateRangeOverlapConstraint = "pricing_rules_date_range_overlap_excl";

    public void Configure(EntityTypeBuilder<PricingRule> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.RuleType).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(r => r.DateRange).HasColumnType("daterange");
        // numeric(12,3), matching ConfigureMoney's mapping and every other
        // monetary column: KWD has three minor-unit digits (CurrencyMinorUnits,
        // docs/adr/0015), and a narrower scale makes Postgres round 10.125 to
        // 10.13 silently. Spelled out rather than reusing ConfigureMoney because
        // this is a bare decimal?, not a Money: an override inherits the unit's
        // currency instead of carrying its own.
        builder.Property(r => r.OverridePrice).HasColumnType("numeric(12,3)");

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

        // Two rules with the same MinNights are indistinguishable to PricingCalculator, which
        // would take whichever the planner returned first. A data invariant rather than a
        // handler's rule: it holds however a row arrives, and both handlers run at the default
        // isolation level because it does (docs/adr/0012).
        //
        // Partial on the soft-delete predicate, same pattern as ix_promotions_code: an archived rule
        // must not block creating its replacement. rule_type is compared as text because it is stored
        // via HasConversion<string>(), unlike status.
        builder.HasIndex(r => new { r.UnitId, r.MinNights }, LengthOfStayTierIndex)
            .IsUnique()
            .HasFilter($"rule_type = 'LengthOfStayDiscount' AND {SoftDelete.NotArchived}")
            .HasDatabaseName(LengthOfStayTierIndex);

        // "At most one active day-of-week multiplier per unit per weekday". EnsureNoDayOfWeekConflict
        // is a read-then-insert: at ReadCommitted, six concurrent overlapping Saturday multipliers all
        // committed, and PricingCalculator takes the first match per night - a price decided by row
        // order.
        //
        // One partial unique index per weekday rather than an exclusion constraint over the array: the
        // exclusion form needs intarray, which redefines &&, @> and <@ for every int4[] in the
        // database. The domain is seven fixed values, so "no two active rules share a day" is "each
        // day is in at most one active rule", which the built-in @> expresses over btree - and a
        // violation names the day that collided.
        //
        // The indexes only hold while days_of_week stays inside 0..6, so the domain is checked here
        // too rather than resting on PricingRule.ValidateDaysOfWeek alone.
        for (int day = 0; day <= 6; day++)
        {
            string name = DayOfWeekIndexName(day);

            builder.HasIndex(r => r.UnitId, name)
                .IsUnique()
                .HasFilter($"rule_type = 'DayOfWeekMultiplier' AND {SoftDelete.NotArchived} AND days_of_week @> ARRAY[{day}]")
                .HasDatabaseName(name);
        }

        builder.ToTable("pricing_rules", CatalogModel.Schema, t => t.HasCheckConstraint(
            "ck_pricing_rules_days_of_week_domain",
            "rule_type <> 'DayOfWeekMultiplier' OR (cardinality(days_of_week) > 0 AND days_of_week <@ ARRAY[0,1,2,3,4,5,6])"));

        builder.HasSoftDeleteFilter();
    }

    /// <summary>
    ///     The per-weekday unique index's name - shared with
    ///     PricingRuleOverlapChecker, which recognises a violation by it.
    /// </summary>
    public static string DayOfWeekIndexName(int day) => $"ix_pricing_rules_unit_day_of_week_{day}_active";
}
