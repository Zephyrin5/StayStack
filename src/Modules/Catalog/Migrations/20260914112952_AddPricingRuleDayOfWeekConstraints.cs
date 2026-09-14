using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingRuleDayOfWeekConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_0_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[0]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_1_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[1]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_2_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[2]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_3_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[3]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_4_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[4]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_5_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[5]");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_day_of_week_6_active",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[6]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pricing_rules_days_of_week_domain",
                table: "pricing_rules",
                sql: "rule_type <> 'DayOfWeekMultiplier' OR (cardinality(days_of_week) > 0 AND days_of_week <@ ARRAY[0,1,2,3,4,5,6])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_0_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_1_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_2_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_3_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_4_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_5_active",
                table: "pricing_rules");

            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_day_of_week_6_active",
                table: "pricing_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pricing_rules_days_of_week_domain",
                table: "pricing_rules");
        }
    }
}
