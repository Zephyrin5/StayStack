using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingBookingIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_booking_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_booking_intents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_booking_intents_created_at",
                table: "pending_booking_intents",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_pending_booking_intents_hold_id",
                table: "pending_booking_intents",
                column: "hold_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_booking_intents");
        }
    }
}
