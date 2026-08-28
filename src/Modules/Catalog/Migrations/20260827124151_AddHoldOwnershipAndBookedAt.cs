using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldOwnershipAndBookedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "booked_at",
                table: "unit_availability_holds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "holder_token",
                table: "unit_availability_holds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds",
                column: "holder_token",
                filter: "status IN ('held', 'booked')");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_status_booked_at",
                table: "unit_availability_holds",
                columns: new[] { "status", "booked_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds");

            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_status_booked_at",
                table: "unit_availability_holds");

            migrationBuilder.DropColumn(
                name: "booked_at",
                table: "unit_availability_holds");

            migrationBuilder.DropColumn(
                name: "holder_token",
                table: "unit_availability_holds");
        }
    }
}
