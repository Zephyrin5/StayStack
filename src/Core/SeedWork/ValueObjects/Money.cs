using SeedWork.Enums;
namespace SeedWork.ValueObjects;

/// <summary>
///     A currency amount, always already rounded to its own currency's
///     minor-unit precision (CurrencyMinorUnits) - Of is the only
///     constructor, and every operator re-rounds its result, so a Money
///     value in the wild is never an unrounded intermediate. See
///     docs/adr/0015 for the domain-only scope boundary (response DTOs
///     stay plain decimal + Currency).
///
///     Because every operator rounds, Money arithmetic is NOT associative
///     like plain decimal: `money * a / b` and `money * (a / b)` can
///     differ, since the first rounds once after multiplying by `a`, the
///     second rounds the division first in plain decimal, then rounds once
///     more. Prefer collapsing a percentage to a single plain-decimal
///     factor first (`money * (percent / 100m)`) rather than chaining
///     Money operators.
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
