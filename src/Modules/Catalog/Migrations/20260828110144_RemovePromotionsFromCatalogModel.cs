using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <inheritdoc />
    public partial class RemovePromotionsFromCatalogModel : Migration
    {
        // Deliberately empty: Promotion/PromotionRedemption moved to the new
        // Promotions module's own AppPromotionsDbContext, not away from the
        // database - the physical tables (promotions, promotion_redemptions)
        // stay exactly where they are, since every module shares one
        // Postgres schema (no HasDefaultSchema anywhere in this codebase).
        // Scaffolding this migration normally would have emitted DropTable
        // for both, because Catalog's own model snapshot no longer includes
        // them - but Promotions' own initial migration (see
        // AddPromotionsToPromotionsModel) is equally empty for the mirror
        // reason, so between the two, nothing physically happens. Same
        // "bring the tracked model in line with reality, not the database"
        // pattern as Transactions/Migrations/
        // 20260823215246_MoveActiveTransactionIndexIntoModel.cs - see
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
