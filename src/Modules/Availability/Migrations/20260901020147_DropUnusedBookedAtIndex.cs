using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Availability.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedBookedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_status_booked_at",
                table: "unit_availability_holds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_status_booked_at",
                table: "unit_availability_holds",
                columns: new[] { "status", "booked_at" });
        }
    }
}
