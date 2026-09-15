using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class DropCheckoutIdempotencyCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_checkout_idempotency_completed_at",
                table: "checkout_idempotency_records");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "checkout_idempotency_records");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_created_at",
                table: "checkout_idempotency_records",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_checkout_idempotency_created_at",
                table: "checkout_idempotency_records");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "checkout_idempotency_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_checkout_idempotency_completed_at",
                table: "checkout_idempotency_records",
                column: "completed_at");
        }
    }
}
