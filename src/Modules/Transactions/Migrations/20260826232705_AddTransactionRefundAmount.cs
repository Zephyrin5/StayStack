using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Transactions.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionRefundAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "refund_amount",
                table: "transactions",
                type: "numeric(10,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_amount",
                table: "transactions");
        }
    }
}
