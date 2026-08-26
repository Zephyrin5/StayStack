using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pricing_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    date_range = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: true),
                    override_price = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    days_of_week = table.Column<int[]>(type: "integer[]", nullable: true),
                    multiplier = table.Column<decimal>(type: "numeric(5,3)", nullable: true),
                    min_nights = table.Column<int>(type: "integer", nullable: true),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricing_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pricing_rules_unit_type",
                table: "pricing_rules",
                columns: new[] { "unit_id", "rule_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pricing_rules");
        }
    }
}
