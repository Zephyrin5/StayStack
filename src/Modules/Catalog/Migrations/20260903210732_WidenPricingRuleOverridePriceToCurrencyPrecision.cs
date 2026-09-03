using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Migrations
{
    /// <summary>
    ///     Widens pricing_rules.override_price from numeric(10,2) to
    ///     numeric(12,3), the precision every other monetary column in this
    ///     app already uses (see ModelBuilderExtensions.ConfigureMoney).
    ///     <para>
    ///         Two decimal places is a currency assumption the rest of the
    ///         codebase does not make. KWD has three minor-unit digits
    ///         (CurrencyMinorUnits, docs/adr/0015), so a 10.125 KWD override
    ///         was valid everywhere except in storage, where Postgres rounded
    ///         it to 10.13 on the way in - a rounding rule, not an error, so
    ///         nothing reported it. The same unit's base price kept its third
    ///         digit, which is what made the inconsistency invisible.
    ///     </para>
    ///     <para>
    ///         Up is lossless in every case: widening scale turns a stored
    ///         10.13 into 10.130 and cannot round anything. EF's "may result
    ///         in the loss of data" warning at scaffold time is about Down,
    ///         which narrows back and genuinely would round a third digit
    ///         away - unavoidable, and the reason this direction should be
    ///         treated as one-way for any deployment that has since written a
    ///         three-decimal override.
    ///     </para>
    /// </summary>
    public partial class WidenPricingRuleOverridePriceToCurrencyPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "override_price",
                table: "pricing_rules",
                type: "numeric(12,3)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "override_price",
                table: "pricing_rules",
                type: "numeric(10,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)",
                oldNullable: true);
        }
    }
}
