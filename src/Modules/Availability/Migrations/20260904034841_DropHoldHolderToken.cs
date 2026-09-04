using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Availability.Migrations
{
    /// <summary>
    ///     Drops unit_availability_holds.holder_token, along with the
    ///     staystack_hold_session cookie that fed it.
    ///     <para>
    ///         The token was written on every hold and never read back. It
    ///         once carried the concurrent-hold cap, but that was a cap the
    ///         caller chose - the cookie is client-supplied, so discarding it
    ///         minted a fresh budget - and docs/adr/0016 moved the cap onto
    ///         ClientNetworkKey. What remained was a per-browser correlator
    ///         kept for an ownership feature ("release my own hold") that was
    ///         never built: no lookup, no cap, no authorization decision, in
    ///         production or in tests.
    ///     </para>
    ///     <para>
    ///         So it was pure cost on both sides of the boundary - a column
    ///         written on every hold, and an opaque per-browser identifier
    ///         set on anonymous visitors' machines and retained for the life
    ///         of every hold row. Removing it takes away state and privacy
    ///         surface without weakening anything, since nothing was relying
    ///         on it.
    ///     </para>
    ///     <para>
    ///         Down restores the column but not the data, which is
    ///         irreversible and deliberately so: the values are opaque
    ///         correlators with no reader, and re-creating them would mean
    ///         re-creating the thing this removes. If hold ownership is ever
    ///         built, it wants a token minted for that purpose with its own
    ///         lifetime, not these.
    ///     </para>
    /// </summary>
    public partial class DropHoldHolderToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "holder_token",
                table: "unit_availability_holds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "holder_token",
                table: "unit_availability_holds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
