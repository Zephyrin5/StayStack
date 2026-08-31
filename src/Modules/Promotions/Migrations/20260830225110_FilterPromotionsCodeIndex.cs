using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Promotions.Migrations
{
    /// <inheritdoc />
    public partial class FilterPromotionsCodeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_promotions_code",
                table: "promotions");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_code",
                table: "promotions",
                column: "code",
                unique: true,
                filter: "status <> 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_promotions_code",
                table: "promotions");

            migrationBuilder.CreateIndex(
                name: "ix_promotions_code",
                table: "promotions",
                column: "code",
                unique: true);
        }
    }
}
