using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Availability.Migrations
{
    /// <summary>
    ///     Adds the CHECK constraint that pins unit_availability_holds.status
    ///     to its closed set, now that the set has three members rather than
    ///     two ('pending_payment' sits between 'held' and 'booked' - see
    ///     HoldStatuses).
    ///     <para>
    ///         Empty otherwise: status is a plain varchar, so widening the set
    ///         needs no schema change. That is exactly the problem this
    ///         constraint addresses. The value is compared as a bare literal
    ///         in raw SQL, in EF expressions and in partial-index filters
    ///         across a dozen sites, none of which the compiler checks - so a
    ///         predicate left behind when the set changes fails silently. The
    ///         constraint cannot catch a stale <em>read</em> predicate, but it
    ///         catches a stale <em>write</em> at the point it happens rather
    ///         than in whatever queries later disagree about the row, and it
    ///         states the closed set in the schema where the next person
    ///         reading the table will find it.
    ///     </para>
    ///     <para>
    ///         No data backfill here, deliberately. Existing 'booked' rows are
    ///         left alone even though most of them represent unpaid bookings
    ///         that now ought to be 'pending_payment'. Converting them wholesale
    ///         would be wrong for the ones whose booking really was paid
    ///         (Confirmed via MarkTransactionSucceeded): those would land in
    ///         'pending_payment' permanently, since the expiry sweep only
    ///         considers Pending bookings and would never revisit them.
    ///         Deciding per row needs the booking's status, which lives in
    ///         another module's table.
    ///     </para>
    ///     <para>
    ///         It is also unnecessary. The companion Bookings migration gives
    ///         every Pending booking a PaymentDueAt, and ExpireUnpaidBookings-
    ///         Job releases holds through ReleaseHoldAsync, which matches
    ///         'booked' as well as 'pending_payment'. So the stale rows are
    ///         reclaimed by the ordinary sweep, driven by the booking's own
    ///         state rather than by a guess made in a migration - and a paid
    ///         booking's hold is never touched.
    ///     </para>
    /// </summary>
    public partial class AddPendingPaymentHoldStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT VALID would skip checking existing rows. Not used: every
            // value in the table today is already 'held' or 'booked', so the
            // validation scan can only pass, and a constraint that has
            // actually been verified is worth more than a marginally faster
            // migration.
            migrationBuilder.Sql("""
                                 ALTER TABLE "unit_availability_holds"
                                 ADD CONSTRAINT "ck_unit_availability_holds_status"
                                 CHECK (status IN ('held', 'pending_payment', 'booked'));
                                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 ALTER TABLE "unit_availability_holds"
                                 DROP CONSTRAINT "ck_unit_availability_holds_status";
                                 """);
        }
    }
}
