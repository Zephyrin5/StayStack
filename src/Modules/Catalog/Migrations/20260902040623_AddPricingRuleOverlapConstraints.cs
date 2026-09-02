using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingRuleOverlapConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_length_of_stay_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'LengthOfStayDiscount' AND status <> 2");

            // Hand-written: Npgsql's EF Core provider has no fluent API for
            // EXCLUDE USING gist, the same reason unit_availability_holds'
            // overlap constraint is raw SQL in Catalog's Initial migration
            // (see docs/adr/0011). btree_gist is already enabled - it is what
            // lets a plain uuid participate in a gist index alongside a range.
            //
            // Enforces "no two active date-range overrides for one unit may
            // overlap", which PricingRuleOverlapChecker.EnsureNoDateRangeConflict
            // already checks in application code. The point is not that the
            // check is wrong: PricingCalculator resolves a nightly price with
            // FirstOrDefault over an unordered list, so at-most-one-match is a
            // correctness precondition of the READ path, and a precondition of
            // that kind belongs in the schema rather than resting on every
            // writer remembering to call the checker first.
            //
            // WHERE mirrors the checker exactly: only DateRangeOverride rows
            // carry a date_range at all, and an archived rule must not block
            // its own replacement. status is the stored int (2 = Archived);
            // rule_type is text via HasConversion<string>().
            migrationBuilder.Sql(@"
                ALTER TABLE ""pricing_rules""
                ADD CONSTRAINT ""pricing_rules_date_range_overlap_excl""
                EXCLUDE USING gist (
                    ""unit_id"" WITH =,
                    ""date_range"" WITH &&
                ) WHERE (""rule_type"" = 'DateRangeOverride' AND ""status"" <> 2);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""pricing_rules"" DROP CONSTRAINT ""pricing_rules_date_range_overlap_excl"";");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_length_of_stay_active",
                table: "pricing_rules");
        }
    }
}
