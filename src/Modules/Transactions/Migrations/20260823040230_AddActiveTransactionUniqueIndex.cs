using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Transactions.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveTransactionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Pending or Succeeded transaction is the "active" one for a
            // booking - only one may exist at a time. Enforced here, not
            // just in InitiateTransactionHandler's pre-check, since that
            // check alone can't stop two concurrent requests from both
            // passing it and both inserting (see the DbUpdateException
            // catch in the handler that turns a violation of this index
            // into the same TransactionAlreadyInProgressException).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ""ix_transactions_booking_id_active""
                ON transactions (booking_id)
                WHERE transaction_status IN ('Pending', 'Succeeded');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""ix_transactions_booking_id_active"";");
        }
    }
}
