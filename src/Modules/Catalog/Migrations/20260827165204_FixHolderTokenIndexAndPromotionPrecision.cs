using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class FixHolderTokenIndexAndPromotionPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds");

            migrationBuilder.AlterColumn<decimal>(
                name: "discount_value",
                table: "promotions",
                type: "numeric(12,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "promotions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character(3)",
                oldFixedLength: true,
                oldMaxLength: 3,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds",
                column: "holder_token",
                filter: "status = 'held'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds");

            migrationBuilder.AlterColumn<decimal>(
                name: "discount_value",
                table: "promotions",
                type: "numeric(10,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "promotions",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds",
                column: "holder_token",
                filter: "status IN ('held', 'booked')");
        }
    }
}
