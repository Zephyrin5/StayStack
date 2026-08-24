using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldExpirationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_hold_expires_at",
                table: "unit_availability_holds",
                column: "hold_expires_at",
                filter: "status = 'held'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_hold_expires_at",
                table: "unit_availability_holds");
        }
    }
}
