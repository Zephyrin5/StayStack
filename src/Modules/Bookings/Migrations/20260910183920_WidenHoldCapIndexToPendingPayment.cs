using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class WidenHoldCapIndexToPendingPayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds",
                column: "client_key",
                filter: "status IN ('held', 'pending_payment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds",
                column: "client_key",
                filter: "status = 'held'");
        }
    }
}
