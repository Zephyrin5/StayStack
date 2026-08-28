using SeedWork.Enums;
namespace SeedWork.ValueObjects;

/// <summary>
///     Thrown by Money's own arithmetic (+/-) when the two operands carry
///     different currencies - an internal invariant violation (this app
///     computed two amounts in different currencies and tried to combine
///     them), not a caller input error, so this is a plain
///     InvalidOperationException rather than an AppException with an HTTP
///     status - see docs/adr/0015.
/// </summary>
public sealed class CurrencyMismatchException(Currency left, Currency right)
    : InvalidOperationException($"Cannot combine amounts in different currencies ({left} and {right}).");
