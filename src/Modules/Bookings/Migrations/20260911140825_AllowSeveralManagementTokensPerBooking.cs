using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AllowSeveralManagementTokensPerBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_booking_management_tokens_booking_id",
                table: "booking_management_tokens");

            migrationBuilder.CreateIndex(
                name: "ix_booking_management_tokens_booking_id",
                table: "booking_management_tokens",
                column: "booking_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_booking_management_tokens_booking_id",
                table: "booking_management_tokens");

            migrationBuilder.CreateIndex(
                name: "ix_booking_management_tokens_booking_id",
                table: "booking_management_tokens",
                column: "booking_id",
                unique: true);
        }
    }
}
