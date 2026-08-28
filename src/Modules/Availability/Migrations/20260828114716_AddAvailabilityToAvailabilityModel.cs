using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Availability.Migrations
{
    /// <inheritdoc />
    public partial class AddAvailabilityToAvailabilityModel : Migration
    {
        // The CreateTable/CreateIndex calls EF scaffolded here are
        // deliberately removed - unit_availability_holds (with its indexes
        // and its raw-SQL GIST exclusion constraint, see docs/adr/0010)
        // already exists physically, created by Catalog's original
        // migrations before this module existed; see Catalog/Migrations/
        // RemoveAvailabilityFromCatalogModel's own comment for the other
        // half of this ownership handoff. Re-running CreateTable against a
        // table that already exists would fail with a real Postgres
        // duplicate-object error the first time this migration runs
        // against a database that already has Catalog's original table.
        //
        // The btree_gist extension statement is real DDL and stays - every
        // module's own migration history independently declares it via
        // StayStackDbContext.OnStayStackModelCreating (Catalog, Bookings,
        // Hosts, Transactions, Reviews, Promotions all do the same in their
        // own first migration), and CREATE EXTENSION IF NOT EXISTS is
        // idempotent across as many migration histories as declare it.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
