using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "refund_obligations",
                columns: table => new
                {
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    policy_refund_amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cause = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_obligations", x => x.booking_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refund_obligations_unresolved",
                table: "refund_obligations",
                column: "cancelled_at",
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refund_obligations");
        }
    }
}
