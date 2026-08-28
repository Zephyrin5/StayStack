using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyAndPromotionReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_promotion_redemptions_promotion_email",
                table: "promotion_redemptions");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "units",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(3)",
                oldFixedLength: true,
                oldMaxLength: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "base_price",
                table: "units",
                type: "numeric(12,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "total_price",
                table: "unit_availability_holds",
                type: "numeric(12,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "length_of_stay_discount_amount",
                table: "unit_availability_holds",
                type: "numeric(12,3)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "unit_availability_holds",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(3)",
                oldFixedLength: true,
                oldMaxLength: 3);

            migrationBuilder.AddColumn<decimal>(
                name: "subtotal",
                table: "unit_availability_holds",
                type: "numeric(12,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<decimal>(
                name: "discount_amount",
                table: "promotion_redemptions",
                type: "numeric(12,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "promotion_redemptions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(3)",
                oldFixedLength: true,
                oldMaxLength: 3);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reversed_at",
                table: "promotion_redemptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_promotion_email",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "guest_email" },
                unique: true,
                filter: "reversed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_promotion_redemptions_promotion_email",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "subtotal",
                table: "unit_availability_holds");

            migrationBuilder.DropColumn(
                name: "reversed_at",
                table: "promotion_redemptions");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "units",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "base_price",
                table: "units",
                type: "numeric(10,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)");

            migrationBuilder.AlterColumn<decimal>(
                name: "total_price",
                table: "unit_availability_holds",
                type: "numeric(10,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)");

            migrationBuilder.AlterColumn<decimal>(
                name: "length_of_stay_discount_amount",
                table: "unit_availability_holds",
                type: "numeric(10,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "unit_availability_holds",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "discount_amount",
                table: "promotion_redemptions",
                type: "numeric(10,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                table: "promotion_redemptions",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_promotion_email",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "guest_email" },
                unique: true);
        }
    }
}
