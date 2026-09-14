namespace Transactions.Contracts;

/// <summary>
///     Where a booking's refund stands, as a caller outside Transactions may
///     report it.
///     <para>
///         A status rather than a boolean because the boolean could not say the
///         one thing a guest most needs to know. RefundPending: false was the
///         answer for Refunded and for RefundFailed alike, so a refund that had
///         gone back to the card and one the provider had refused looked the
///         same from the outside - and only one of them is finished.
///     </para>
/// </summary>
public enum RefundStatus
{
    /// <summary>No refund exists and none is owed.</summary>
    None = 0,

    /// <summary>Owed or requested, and not yet settled.</summary>
    Pending = 1,

    /// <summary>The money went back.</summary>
    Refunded = 2,

    /// <summary>
    ///     The refund was attempted and did not go through. Not finished - money
    ///     is still owed - but nothing will move it forward on its own yet.
    /// </summary>
    Failed = 3
}
