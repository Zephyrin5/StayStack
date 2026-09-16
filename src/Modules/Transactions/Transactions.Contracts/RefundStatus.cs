namespace Transactions.Contracts;

/// <summary>
///     Where a booking's refund stands, as a caller outside Transactions may report it. A status,
///     not a boolean: a refund that reached the card and one the provider refused are both "not
///     pending", and only one of them is finished.
/// </summary>
public enum RefundStatus
{
    /// <summary>No refund exists and none is owed.</summary>
    None = 0,

    /// <summary>Owed or requested, and not yet settled.</summary>
    Pending = 1,

    /// <summary>The money went back.</summary>
    Refunded = 2,

    /// <summary>Attempted and refused. Money is still owed, and nothing retries it yet.</summary>
    Failed = 3
}
