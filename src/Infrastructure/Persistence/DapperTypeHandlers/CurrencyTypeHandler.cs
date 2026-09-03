using Dapper;
using SeedWork.Enums;
using System.Data;
namespace Persistence.DapperTypeHandlers;

/// <summary>
///     Maps the <c>character(3)</c> currency column onto
///     <see cref="Currency"/>, so Dapper row types can declare the enum
///     directly instead of a string every caller has to parse.
///     <para>
///         EF already does this through <c>HasConversion&lt;string&gt;()</c>
///         (see docs/adr/0015 for why currency is stored as its code rather
///         than an ordinal). Dapper does not read EF's model, so the two
///         Tier 3 read sites - <c>GetPriceCalendarHandler</c> and
///         <c>HoldConfirmation</c> - each restated the conversion inline as
///         <c>Enum.Parse&lt;Currency&gt;(row.Currency.Trim())</c>. Same
///         reasoning that already put DateOnly and NpgsqlRange behind
///         handlers: the conversion belongs once, next to the column shape
///         that explains it.
///     </para>
/// </summary>
public class CurrencyTypeHandler : SqlMapper.TypeHandler<Currency>
{
    public override void SetValue(IDbDataParameter parameter, Currency value)
    {
        parameter.Value = value.ToString();
        parameter.DbType = DbType.StringFixedLength;
        parameter.Size = 3;
    }

    public override Currency Parse(object value)
    {
        return value switch
        {
            Currency c => c,
            // Trimmed because the column is character(3), fixedLength - a
            // blank-padded type, where Postgres pads any value shorter than
            // the declared width. Inert for every code that can currently be
            // stored, since KWD/SAR/AED/USD are all exactly three characters
            // and Currency.None (four) cannot fit the column at all. It is
            // here rather than at the call sites so that the guard sits with
            // the column shape that motivates it, instead of reading as an
            // unexplained defensive .Trim() beside a parse.
            string s => Enum.Parse<Currency>(s.Trim()),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Currency.")
        };
    }
}
