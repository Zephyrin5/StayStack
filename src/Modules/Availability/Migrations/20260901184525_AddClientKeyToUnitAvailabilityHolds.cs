using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Availability.Migrations
{
    /// <inheritdoc />
    public partial class AddClientKeyToUnitAvailabilityHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds");

            migrationBuilder.AddColumn<string>(
                name: "client_key",
                table: "unit_availability_holds",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds",
                column: "client_key",
                filter: "status = 'held'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_availability_holds_client_key_active",
                table: "unit_availability_holds");

            migrationBuilder.DropColumn(
                name: "client_key",
                table: "unit_availability_holds");

            migrationBuilder.CreateIndex(
                name: "ix_unit_availability_holds_holder_token_active",
                table: "unit_availability_holds",
                column: "holder_token",
                filter: "status = 'held'");
        }
    }
}
