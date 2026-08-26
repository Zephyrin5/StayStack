using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitCancellationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is deliberately the real serialized Moderate
            // default (see CancellationPolicy.CreateDefault()), not a
            // placeholder needing a follow-up backfill UPDATE - Postgres
            // applies a column DEFAULT to every existing row when adding a
            // NOT NULL column, so this single AddColumn already leaves
            // every pre-existing unit with a real, valid policy. A
            // deliberate exception to docs/adr/0011's "prefer model config"
            // default: a NOT NULL column on existing data has no way to
            // get its default from model config alone. Hand-written, not
            // computed by calling CancellationPolicy.CreateDefault() at
            // migration-run time - a migration's effect should stay fixed
            // even if that method's own default shape changes later.
            migrationBuilder.AddColumn<string>(
                name: "cancellation_policy",
                table: "units",
                type: "jsonb",
                nullable: false,
                defaultValue: "[{\"MinDaysBeforeCheckIn\":5,\"RefundPercent\":100},{\"MinDaysBeforeCheckIn\":1,\"RefundPercent\":50},{\"MinDaysBeforeCheckIn\":0,\"RefundPercent\":0}]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_policy",
                table: "units");
        }
    }
}
