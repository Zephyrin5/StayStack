using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
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
                UPDATE "bookings_outbox_messages"
                SET type = 'bookings.release-hold.v1'
                WHERE type = 'ReleaseHoldOutboxMessage';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "bookings_outbox_messages"
                SET type = 'bookings.reverse-transaction.v1'
                WHERE type = 'ReverseTransactionOutboxMessage';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "bookings_outbox_messages"
                SET type = 'bookings.reverse-promotion-redemption.v1'
                WHERE type = 'ReverseRedemptionOutboxMessage';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible, so rolling back to a deployment that still switches
            // on CLR names leaves its rows routable.
            migrationBuilder.Sql(
                """
                UPDATE "bookings_outbox_messages"
                SET type = 'ReleaseHoldOutboxMessage'
                WHERE type = 'bookings.release-hold.v1';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "bookings_outbox_messages"
                SET type = 'ReverseTransactionOutboxMessage'
                WHERE type = 'bookings.reverse-transaction.v1';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "bookings_outbox_messages"
                SET type = 'ReverseRedemptionOutboxMessage'
                WHERE type = 'bookings.reverse-promotion-redemption.v1';
                """);
        }
    }
}
