using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.SeedWork;

// Money is the newest primitive in the codebase (docs/adr/0015) and is now
// load-bearing for every amount in the system, with genuinely surprising
// semantics of its own: non-associative arithmetic, operators that throw on
// currency mismatch, a factory that rejects Currency.None, and a per-
// currency rounding scale. Each deserves direct coverage here rather than
// being inferred from whatever a caller's own tests happen to exercise -
// PricingCalculatorTests, for example, never specifically proves the KWD
// 3-decimal case.
public class MoneyTests
{
    [Theory]
    [InlineData(10.1234, Currency.KWD, 10.123)] // 4th decimal < 5 - rounds down
    [InlineData(10.1236, Currency.KWD, 10.124)] // 4th decimal >= 5 - rounds up
    [InlineData(10.126, Currency.USD, 10.13)] // 3rd decimal >= 5 - rounds up
    [InlineData(10.124, Currency.USD, 10.12)] // 3rd decimal < 5 - rounds down
    [InlineData(10.126, Currency.SAR, 10.13)]
    [InlineData(10.126, Currency.AED, 10.13)]
    public void Of_RoundsToTheCurrencysOwnMinorUnitScale(decimal amount, Currency currency, decimal expected)
    {
        Money money = Money.Of(amount, currency);

        Assert.Equal(expected, money.Amount);
    }

    [Fact]
    public void Of_KeepsThreeDecimalPlaces_ForKwdSpecifically()
    {
        // The one currency this app supports that isn't 2-decimal - the
        // whole reason CurrencyMinorUnits exists instead of a single
        // hardcoded scale everywhere. A 2-decimal assumption here would
        // silently truncate a real fraction of KWD's smallest unit.
        Money money = Money.Of(1.999m, Currency.KWD);

        Assert.Equal(1.999m, money.Amount);
    }

    [Theory]
    [InlineData(0.125, Currency.USD, 0.12)] // midpoint between .12/.13 - .12 is even
    [InlineData(0.135, Currency.USD, 0.14)] // midpoint between .13/.14 - .14 is even
    [InlineData(0.145, Currency.SAR, 0.14)] // midpoint between .14/.15 - .14 is even
    [InlineData(0.155, Currency.AED, 0.16)] // midpoint between .15/.16 - .16 is even
    public void Of_RoundsExactMidpoints_ToEven_NotAwayFromZero(decimal amount, Currency currency, decimal expected)
    {
        // MidpointRounding.ToEven (banker's rounding), not the more
        // commonly assumed round-half-away-from-zero - a caller expecting
        // "0.125 always rounds up to 0.13" would be wrong here, and this is
        // exactly the kind of surprising default worth locking down with a
        // real exact-midpoint case rather than trusting the doc comment.
        Money money = Money.Of(amount, currency);

        Assert.Equal(expected, money.Amount);
    }

    [Theory]
    [InlineData(0.1235, 0.124)] // midpoint between .123/.124 - .124 is even
    [InlineData(0.1225, 0.122)] // midpoint between .122/.123 - .122 is even
    public void Of_RoundsExactMidpoints_ToEven_AtKwdsThreeDecimalScale(decimal amount, decimal expected)
    {
        Money money = Money.Of(amount, Currency.KWD);

        Assert.Equal(expected, money.Amount);
    }

    [Fact]
    public void Of_Throws_WhenCurrencyIsNone()
    {
        // None exists specifically so it can never be constructed into a
        // real Money value - see Currency.cs's own doc comment on why 0
        // isn't a real currency ordinal. This is the enforcement point.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => Money.Of(10m, Currency.None));
        Assert.Equal("currency", ex.ParamName);
    }

    [Fact]
    public void Default_Money_HasNoneCurrency_NotASilentlyValidZeroAmount()
    {
        // The entire reason Currency was renumbered (None = 0) instead of
        // KWD = 0 - a default-constructed Money (array allocation, a
        // deserializer, an EF materialization edge case on a nullable
        // complex property) must be detectable as not-a-real-value, not
        // mistaken for "0 KWD". Money.Of itself can never produce this -
        // only default(Money)/new Money() can, since the parameterized
        // constructor is private.
        Money money = default;

        Assert.Equal(Currency.None, money.Currency);
        Assert.Equal(0m, money.Amount);
    }

    [Fact]
    public void Addition_SameCurrency_Sums()
    {
        // Both operands are already rounded to KWD's own 3-decimal scale by
        // construction, so their exact sum is always representable at that
        // same scale - the Round inside operator+ is a defensive no-op
        // here, not something this case can actually observe changing the
        // value. It's the multiply/divide operators (below) where an
        // operator's own rounding is actually load-bearing.
        Money left = Money.Of(10.005m, Currency.KWD);
        Money right = Money.Of(0.001m, Currency.KWD);

        Money result = left + right;

        Assert.Equal(Money.Of(10.006m, Currency.KWD), result);
    }

    [Fact]
    public void Subtraction_SameCurrency_Subtracts()
    {
        Money left = Money.Of(10.005m, Currency.KWD);
        Money right = Money.Of(0.001m, Currency.KWD);

        Money result = left - right;

        Assert.Equal(Money.Of(10.004m, Currency.KWD), result);
    }

    [Fact]
    public void Addition_DifferentCurrencies_ThrowsCurrencyMismatchException()
    {
        Money kwd = Money.Of(10m, Currency.KWD);
        Money usd = Money.Of(10m, Currency.USD);

        CurrencyMismatchException ex = Assert.Throws<CurrencyMismatchException>(() => kwd + usd);
        Assert.Contains("KWD", ex.Message);
        Assert.Contains("USD", ex.Message);
    }

    [Fact]
    public void Subtraction_DifferentCurrencies_ThrowsCurrencyMismatchException()
    {
        Money kwd = Money.Of(10m, Currency.KWD);
        Money usd = Money.Of(10m, Currency.USD);

        Assert.Throws<CurrencyMismatchException>(() => kwd - usd);
    }

    [Fact]
    public void Multiplication_ByPlainDecimalFactor_RoundsTheResult()
    {
        Money money = Money.Of(10m, Currency.USD);

        Money result = money * 0.333m;

        Assert.Equal(Money.Of(3.33m, Currency.USD), result);
    }

    [Fact]
    public void Division_ByPlainDecimalFactor_RoundsTheResult()
    {
        Money money = Money.Of(10m, Currency.USD);

        Money result = money / 3m;

        Assert.Equal(Money.Of(3.33m, Currency.USD), result);
    }

    [Fact]
    public void Arithmetic_IsNotAssociative_BecauseEveryOperatorRoundsItsOwnResult()
    {
        // The exact scenario Money's own doc comment (and PricingCalculator/
        // CancelBookingHandler's real-world bug from this same review round)
        // warns about: chaining a Money multiply into a Money divide rounds
        // twice, at two different scales of the same value, and can produce
        // a materially different result than collapsing the factor to a
        // single plain-decimal fraction first.
        //
        // Worked by hand, no rounding ambiguity in either path:
        //   money * a       ->  1.00 * 0.126 = 0.126 -> rounds to 0.13 (2dp)
        //   (0.13) / b      ->  0.13 / 0.01 = 13.00
        // versus
        //   a / b           ->  0.126 / 0.01 = 12.6 (exact, plain decimal)
        //   money * (a / b) ->  1.00 * 12.6 = 12.60 (one rounding, at the end)
        Money money = Money.Of(1m, Currency.USD);
        decimal a = 0.126m;
        decimal b = 0.01m;

        Money chained = money * a / b;
        Money collapsedFirst = money * (a / b);

        Assert.Equal(Money.Of(13.00m, Currency.USD), chained);
        Assert.Equal(Money.Of(12.60m, Currency.USD), collapsedFirst);
        Assert.NotEqual(chained, collapsedFirst);
    }

    [Fact]
    public void Equality_SameAmountAndCurrency_AreEqual()
    {
        Assert.Equal(Money.Of(10m, Currency.KWD), Money.Of(10m, Currency.KWD));
    }

    [Fact]
    public void Equality_SameAmountDifferentCurrency_AreNotEqual()
    {
        // Money's equality is structural (Amount and Currency both), not
        // amount-only - two values that look the same numerically but are
        // denominated differently must never compare equal.
        Assert.NotEqual(Money.Of(10m, Currency.KWD), Money.Of(10m, Currency.USD));
    }
}
