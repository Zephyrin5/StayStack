using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Transactions.Migrations
{
    /// <inheritdoc />
    public partial class MoveActiveTransactionIndexIntoModel : Migration
    {
        // Deliberately empty: this migration exists only to bring the
        // tracked model/snapshot in line with reality (the index moved from
        // hand-written SQL to TransactionConfiguration's fluent HasFilter),
        // not to apply new DDL. AddActiveTransactionUniqueIndex already
        // created this exact index physically - scaffolding this migration
        // normally would have re-issued CREATE INDEX for an index that
        // already exists (confirmed: it does, with a 42P07 duplicate-object
        // error) since the model snapshot never knew about the hand-written
        // SQL version to diff against.

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
