using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingTimeZone : Migration
    {
        // Same two-step shape, and the same reasoning, as Catalog's
        // AddPropertyTimeZone: backfill existing rows with the app's actual
        // market so the NOT NULL column can land, then drop the schema-level
        // default so nothing inserting outside EF silently inherits it.
        //
        // Backfilling here rather than leaving the column nullable is the
        // point of docs/adr/0018: a null zone would fall back to UTC at read
        // time, which is the exact defect being removed. A pre-ADR booking
        // gets a guessed-but-plausible zone instead of a guaranteed-wrong one.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                table: "bookings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Asia/Kuwait");

            migrationBuilder.AlterColumn<string>(
                name: "time_zone_id",
                table: "bookings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: false,
                oldDefaultValue: "Asia/Kuwait");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "time_zone_id",
                table: "bookings");
        }
    }
}
