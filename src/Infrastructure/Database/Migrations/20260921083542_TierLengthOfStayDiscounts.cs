using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class TierLengthOfStayDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_length_of_stay_active",
                schema: "catalog",
                table: "pricing_rules");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_min_nights_active",
                schema: "catalog",
                table: "pricing_rules",
                columns: new[] { "unit_id", "min_nights" },
                unique: true,
                filter: "rule_type = 'LengthOfStayDiscount' AND status <> 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pricing_rules_unit_min_nights_active",
                schema: "catalog",
                table: "pricing_rules");

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_length_of_stay_active",
                schema: "catalog",
                table: "pricing_rules",
                column: "unit_id",
                unique: true,
                filter: "rule_type = 'LengthOfStayDiscount' AND status <> 2");
        }
    }
}
