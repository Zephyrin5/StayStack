using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Transactions.Migrations
{
    /// <inheritdoc />
    public partial class RenameOutboxTypeDiscriminators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A data migration, not a schema one. The type column used to hold
            // the message's CLR type name, and each dispatcher switched on it -
            // so the identifier a row is routed by was a name an IDE rename
            // could change silently. Rows already in flight carry the old
            // names, and leaving them would ship exactly the failure this
            // change exists to prevent: every undelivered row of each type
            // dead-lettered, one per booking or payment it was compensating.
            //
            // Unfiltered by processed_at on purpose. A resolved row is history
            // somebody will read, and history labelled with an identifier that
            // stopped meaning anything is worse than useless.

            migrationBuilder.Sql(
                """
                UPDATE "transactions_outbox_messages"
                SET type = 'transactions.confirm-booking-payment.v1'
                WHERE type = 'ConfirmBookingPaymentOutboxMessage';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible, so rolling back to a deployment that still switches
            // on CLR names leaves its rows routable.
            migrationBuilder.Sql(
                """
                UPDATE "transactions_outbox_messages"
                SET type = 'ConfirmBookingPaymentOutboxMessage'
                WHERE type = 'transactions.confirm-booking-payment.v1';
                """);
        }
    }
}
