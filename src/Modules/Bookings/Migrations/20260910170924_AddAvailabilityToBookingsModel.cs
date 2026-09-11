using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bookings.Migrations
{
    /// <summary>
    ///     Ownership handoff: unit_availability_holds becomes this module's
    ///     table. The mirror image of the pair that handed it the other way -
    ///     Catalog/RemoveAvailabilityFromCatalogModel and
    ///     Availability/AddAvailabilityToAvailabilityModel - and it carries
    ///     the same trap, so it is written by hand.
    ///     <para>
    ///         <b>This migration depends on Catalog's having been applied
    ///         first</b>, which is a real ordering constraint between two
    ///         modules that otherwise migrate independently. It is stated in
    ///         README.md's migration step and executed by
    ///         IntegrationTestWebApplicationFactory.MigrateAllModulesAsync;
    ///         nothing in EF enforces it, because EF has no notion of one
    ///         history table depending on another.
    ///     </para>
    ///     <para>
    ///         <b>No CreateTable.</b> EF scaffolds one, because as far as the
    ///         Bookings model is concerned this entity is new. The table is
    ///         not: Catalog's original Initial migration created it, along
    ///         with its indexes and the raw-SQL GIST exclusion constraint, and
    ///         nothing has ever dropped it. Left scaffolded, this fails with a
    ///         duplicate-object error on any database that has ever run
    ///         Catalog's Initial - which is all of them.
    ///     </para>
    ///     <para>
    ///         <b>But not empty either, unlike that earlier pair.</b> Those
    ///         two were pure ownership moves. Availability went on to make
    ///         four real schema changes afterwards - dropping an unused
    ///         index, adding client_key with its partial index, adding the
    ///         status CHECK constraint, and dropping holder_token - and
    ///         deleting that module takes its migration history with it. A
    ///         database migrated from empty would never have seen any of
    ///         them, and would end up with a holder_token column, no
    ///         client_key, and no CHECK: a schema the model no longer
    ///         describes.
    ///     </para>
    ///     <para>
    ///         So this replays those four, idempotently. On a fresh database
    ///         they do the work Availability's history used to; on an existing
    ///         one, where that history already ran, every statement is a
    ///         no-op. That is why each is IF EXISTS / IF NOT EXISTS rather
    ///         than the plain DDL EF would emit, and why the constraint needs
    ///         a pg_constraint check - Postgres has no ADD CONSTRAINT IF NOT
    ///         EXISTS.
    ///     </para>
    ///     <para>
    ///         Availability's own __ef_migrations_history_availability table
    ///         is left behind in existing databases. Harmless: nothing reads
    ///         it once the module is gone.
    ///     </para>
    /// </summary>
    public partial class AddAvailabilityToBookingsModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // btree_gist is what lets the exclusion constraint mix an
            // equality column with a range column (docs/adr/0010). CREATE
            // EXTENSION IF NOT EXISTS is idempotent across migration
            // histories, which is why declaring it in a second module is safe.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            // Was Availability/DropUnusedBookedAtIndex.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "ix_unit_availability_holds_status_booked_at";""");

            // Was Availability/AddClientKeyToUnitAvailabilityHolds. The cap
            // moved off the client-supplied hold cookie onto the connection's
            // peer address (docs/adr/0016), so the old holder_token index went
            // with it.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "ix_unit_availability_holds_holder_token_active";""");
            migrationBuilder.Sql("""
                                 ALTER TABLE "unit_availability_holds"
                                 ADD COLUMN IF NOT EXISTS client_key character varying(45);
                                 """);
            migrationBuilder.Sql("""
                                 CREATE INDEX IF NOT EXISTS "ix_unit_availability_holds_client_key_active"
                                 ON "unit_availability_holds" (client_key)
                                 WHERE status = 'held';
                                 """);

            // Was Availability/DropHoldHolderToken - a per-browser correlator
            // written on every hold and read by nothing.
            migrationBuilder.Sql("""
                                 ALTER TABLE "unit_availability_holds"
                                 DROP COLUMN IF EXISTS holder_token;
                                 """);

            // Was Availability/AddPendingPaymentHoldStatus. The status is a
            // plain varchar compared as a bare literal in raw SQL, EF
            // expressions and partial-index filters alike, so the closed set
            // is pinned in the schema where the next reader will find it.
            migrationBuilder.Sql("""
                                 DO $$
                                 BEGIN
                                     IF NOT EXISTS (
                                         SELECT 1 FROM pg_constraint
                                         WHERE conname = 'ck_unit_availability_holds_status'
                                     ) THEN
                                         ALTER TABLE "unit_availability_holds"
                                         ADD CONSTRAINT "ck_unit_availability_holds_status"
                                         CHECK (status IN ('held', 'pending_payment', 'booked'));
                                     END IF;
                                 END $$;
                                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately does not drop the table: this migration never
            // created it. Reversing the ownership move is a code change, not a
            // schema one, exactly as it was in the pair this mirrors.
            migrationBuilder.Sql("""
                                 ALTER TABLE "unit_availability_holds"
                                 DROP CONSTRAINT IF EXISTS "ck_unit_availability_holds_status";
                                 """);
        }
    }
}
