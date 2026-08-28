using SeedWork.Enums;
namespace SeedWork.ValueObjects;

/// <summary>
///     A currency amount, always already rounded to its own currency's
///     minor-unit precision (CurrencyMinorUnits) - constructing one that
///     isn't is not possible through Of, and every arithmetic operator
///     re-rounds its result, so a Money value in the wild is always a real,
///     payable amount, never an intermediate unrounded fraction. See
///     docs/adr/0015 for why this exists and the deliberate domain-only
///     scope boundary (response DTOs stay plain decimal + Currency).
///
///     Because every operator rounds its own result, Money arithmetic is
///     NOT associative the way plain decimal is: `money * a / b` and
///     `money * (a / b)` can produce different values, since the first
///     rounds once after the multiplication (at a value scaled by `a`) and
///     the second rounds the division first, in plain decimal, then rounds
///     once more after one Money multiplication. Prefer collapsing a
///     percentage/fraction to a single plain-decimal factor first (`money *
///     (percent / 100m)`), matching PricingCalculator's own shape, rather
///     than chaining Money operators end to end.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, Currency currency)
    {
        if (currency == Currency.None)
        {
            throw new ArgumentException("A Money value must have a real currency.", nameof(currency));
        }

        return new Money(Round(amount, currency), currency);
    }

    public static decimal Round(decimal amount, Currency currency)
    {
        return Math.Round(amount, CurrencyMinorUnits.For(currency), MidpointRounding.ToEven);
    }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return Of(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return Of(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money money, decimal factor)
    {
        return Of(money.Amount * factor, money.Currency);
    }

    public static Money operator /(Money money, decimal divisor)
    {
        return Of(money.Amount / divisor, money.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }
}
