using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyTimeZone : Migration
    {
        // Two statements, deliberately (see docs/adr/0018).
        //
        // The column is NOT NULL, so existing rows need a value, and
        // "Asia/Kuwait" is the honest choice: it is this app's actual market
        // (KWD default currency, Kuwait City throughout), and no better
        // information exists in the data. It is still a guess, and wrong for
        // any property elsewhere - hosts outside Kuwait have to correct
        // theirs. That is a one-time data-migration decision and is the only
        // place a timezone is ever guessed; nothing at read time falls back.
        //
        // Then the default is dropped rather than left in place. EF's model
        // carries no default, but defaultValue on AddColumn persists as a
        // schema-level DEFAULT, so anything inserting outside EF - the
        // hand-written Dapper in this codebase, a manual fix-up - would
        // silently get Kuwait instead of failing. Dropping it makes the
        // column genuinely required at the database level too.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                table: "properties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Asia/Kuwait");

            migrationBuilder.AlterColumn<string>(
                name: "time_zone_id",
                table: "properties",
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
                table: "properties");
        }
    }
}
