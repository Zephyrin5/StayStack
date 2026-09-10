using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckoutIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "checkout_idempotency_records",
                columns: table => new
                {
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    management_token = table.Column<string>(type: "character varying(86)", maxLength: 86, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkout_idempotency_records", x => x.booking_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_completed_at",
                table: "checkout_idempotency_records",
                column: "completed_at");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_key_hash",
                table: "checkout_idempotency_records",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkout_idempotency_records");
        }
    }
}
