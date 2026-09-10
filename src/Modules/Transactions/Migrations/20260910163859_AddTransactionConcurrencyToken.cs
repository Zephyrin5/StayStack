using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Transactions.Migrations
{
    /// <summary>
    ///     Deliberately empty, and has to be.
    ///     <para>
    ///         TransactionConfiguration now maps <c>xmin</c> as a row-version
    ///         concurrency token so a stale write to a transaction affects
    ///         zero rows rather than silently overwriting a terminal state.
    ///         <c>xmin</c> is a Postgres <em>system</em> column: it exists on
    ///         every table already and cannot be added. EF cannot know that,
    ///         and scaffolds an <c>AddColumn&lt;uint&gt;(name: "xmin", type:
    ///         "xid")</c> that fails with a duplicate-column error against any
    ///         real database.
    ///     </para>
    ///     <para>
    ///         So the scaffolded body is removed and this migration exists
    ///         only to move the model snapshot forward, which is what keeps
    ///         ModelHasNoPendingChangesTests honest. Same shape, and same
    ///         reason, as the deliberately-empty ownership-handoff pair
    ///         Catalog/RemoveAvailabilityFromCatalogModel and
    ///         Availability/AddAvailabilityToAvailabilityModel: the model
    ///         changed, the schema did not.
    ///     </para>
    /// </summary>
    public partial class AddTransactionConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
