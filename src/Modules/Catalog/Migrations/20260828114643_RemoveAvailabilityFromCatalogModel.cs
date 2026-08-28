using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAvailabilityFromCatalogModel : Migration
    {
        // Deliberately empty: UnitAvailabilityHold moved to the new
        // Availability module's own AppAvailabilityDbContext, not away from
        // the database - the physical table (unit_availability_holds, its
        // indexes, and its GIST exclusion constraint) stays exactly where
        // it is, since every module shares one Postgres schema (no
        // HasDefaultSchema anywhere in this codebase). Scaffolding this
        // migration normally would have emitted DropTable, because
        // Catalog's own model snapshot no longer includes the entity - but
        // Availability's own initial migration (see
        // AddAvailabilityToAvailabilityModel) is equally empty for the
        // mirror reason, so between the two, nothing physically happens.
        // Same "bring the tracked model in line with reality, not the
        // database" pattern as Transactions/Migrations/
        // 20260823215246_MoveActiveTransactionIndexIntoModel.cs and
        // Catalog/Migrations/RemovePromotionsFromCatalogModel.cs - see
        // docs/adr/0004's Consequences for the module-ownership move this
        // pair of migrations is closing out.
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
