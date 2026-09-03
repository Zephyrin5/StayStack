using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <summary>
    ///     Gives every booking awaiting payment a deadline, so the unit it is
    ///     holding comes back if nobody pays.
    ///     <para>
    ///         Confirming a checkout moves the hold to 'pending_payment',
    ///         which goes on blocking its range through the exclusion
    ///         constraint. Before this, nothing bounded that: an unpaid
    ///         booking held its unit indefinitely, and an anonymous caller
    ///         could take a calendar apart by repeatedly submitting checkout
    ///         forms without ever paying. ExpireUnpaidBookingsJob reads this
    ///         column, and the index below is its scan.
    ///     </para>
    /// </summary>
    public partial class AddBookingPaymentDueAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payment_due_at",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            // Backfilled for existing Pending bookings, which is the whole
            // population this change exists to reclaim: every booking created
            // to date took its unit off the market, and none can have been
            // paid for, since no payment provider is wired up. Left null they
            // would keep their units indefinitely - invisible to the new
            // sweep, which skips null.
            //
            // now() + the window rather than created_at + the window, so
            // every row gets a full payment window measured from deployment
            // instead of being overdue the instant this runs. That matters
            // for exactly one row - a checkout genuinely in flight as the
            // migration applies - and cancelling that guest out from under
            // themselves is a worse trade than waiting half an hour to
            // reclaim rows already stuck far longer. The resulting expiry
            // burst is bounded by the job's own per-run cap.
            //
            // 30 minutes is BookingLifecyclePolicyOptions.PaymentWindowMinutes'
            // default, restated as a literal because a migration is a
            // historical record: it has to keep meaning this if that
            // configured default is later changed.
            //
            // Confirmed and Cancelled bookings are deliberately untouched and
            // stay null. A Confirmed booking must never acquire a deadline
            // that could reclaim a unit somebody paid for.
            migrationBuilder.Sql("""
                                 UPDATE "bookings"
                                 SET payment_due_at = now() + interval '30 minutes'
                                 WHERE booking_status = 'Pending';
                                 """);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_payment_due_at",
                table: "bookings",
                column: "payment_due_at",
                filter: "booking_status = 'Pending' AND payment_due_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_bookings_payment_due_at",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "payment_due_at",
                table: "bookings");
        }
    }
}
