using SeedWork.Enums;
namespace SeedWork.ValueObjects;

/// <summary>
///     ISO 4217 minor-unit digit count per currency this app actually
///     supports - not every currency uses 2 decimal places (KWD/BHD/OMR/JOD/
///     TND use 3; some currencies, not currently supported here, use 0).
///     The one place this app's Money rounding rule is allowed to depend on
///     which currency it's rounding - see docs/adr/0015.
/// </summary>
public static class CurrencyMinorUnits
{
    private static readonly Dictionary<Currency, int> DecimalDigits = new()
    {
        [Currency.KWD] = 3,
        [Currency.SAR] = 2,
        [Currency.AED] = 2,
        [Currency.USD] = 2
    };

    public static int For(Currency currency)
    {
        return DecimalDigits.TryGetValue(currency, out int digits)
            ? digits
            : throw new ArgumentOutOfRangeException(nameof(currency), currency, "No minor-unit digit count configured for this currency.");
    }
}
