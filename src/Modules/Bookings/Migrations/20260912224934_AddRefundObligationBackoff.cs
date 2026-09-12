using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundObligationBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_refund_obligations_unresolved",
                table: "refund_obligations");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "refund_obligations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "refund_obligations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateIndex(
                name: "ix_refund_obligations_unresolved",
                table: "refund_obligations",
                column: "next_attempt_at",
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_refund_obligations_unresolved",
                table: "refund_obligations");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "refund_obligations");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "refund_obligations");

            migrationBuilder.CreateIndex(
                name: "ix_refund_obligations_unresolved",
                table: "refund_obligations",
                column: "cancelled_at",
                filter: "resolved_at IS NULL");
        }
    }
}
