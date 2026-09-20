using Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Catalog.Entities.Configurations;

public class PricingRuleConfiguration : IEntityTypeConfiguration<PricingRule>
{
    /// <summary>One active length-of-stay discount per unit.</summary>
    public const string LengthOfStayIndex = "ix_pricing_rules_unit_length_of_stay_active";

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
        // Partial on the soft-delete predicate, same pattern as ix_promotions_code: an archived rule
        // must not block creating its replacement. rule_type is compared as text because it is stored
        // via HasConversion<string>(), unlike status.
        builder.HasIndex(r => r.UnitId, LengthOfStayIndex)
            .IsUnique()
            .HasFilter($"rule_type = 'LengthOfStayDiscount' AND {SoftDelete.NotArchived}")
            .HasDatabaseName(LengthOfStayIndex);

        // "At most one active day-of-week multiplier per unit per weekday" - the
        // third overlap invariant, and until these the only one with nothing in
        // the database behind it. EnsureNoDayOfWeekConflict is a read-then-insert:
        // with the handlers' Serializable isolation lowered to ReadCommitted, six
        // concurrent overlapping Saturday multipliers all committed.
        // PricingCalculator takes the first matching multiplier per night, so
        // that is a price decided by row order.
        //
        // One partial unique index per weekday rather than an exclusion
        // constraint over the array. The exclusion form needs intarray, which is
        // not enabled here and which, once installed, redefines &&, @> and <@ for
        // every int4[] in the database with semantics that differ from the
        // built-ins. The domain is seven fixed values, so "no two active rules
        // share a day" is exactly "each day is in at most one active rule", and
        // the built-in @> expresses that with plain btree indexes. A violation
        // also names the day that collided.
        //
        // The indexes only hold if days_of_week stays inside 0..6 - a 7 would
        // fall outside every one of them - so the domain is checked here too
        // rather than resting on PricingRule.ValidateDaysOfWeek alone.
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
