namespace Bookings.Entities;

/// <summary>
///     Lets a client retry <c>POST /api/bookings</c> after a lost
///     acknowledgement and receive the original answer instead of a second
///     booking or a dead end.
///     <para>
///         The dead end is the reason this exists. A confirmation's plaintext
///         management token is generated in memory and returned in the
///         response; only its hash is persisted
///         (<see cref="BookingManagementToken.TokenHash"/>). So a connection
///         dropped after the commit leaves an anonymous guest with a real
///         booking they cannot reach, cancel, or prove is theirs - they never
///         learned its id either. Retrying does not help: the hold has already
///         moved to 'pending_payment', so the retry fails
///         ConfirmHoldAsync's <c>status = 'held'</c> guard and returns 404.
///         The booking then blocks its range until the payment window lapses.
///         Nobody is charged today, and that is exactly the property that
///         stops being true when payment is wired up.
///     </para>
///     <para>
///         Shaped after <see cref="PendingBookingIntent"/> - a row written
///         before the work, keyed by the same pre-generated booking id,
///         resolved by the same transaction that finishes the work. The
///         difference is what happens at the end: an intent is deleted,
///         because it carries nothing <c>Booking</c> does not already record.
///         This row has to survive, because it carries the one thing that
///         exists nowhere else once the response is lost.
///     </para>
/// </summary>
public sealed class CheckoutIdempotencyRecord
{
    /// <summary>
    ///     How long a completed record is replayable. After this it is purged
    ///     and a retry with the same key starts a fresh confirmation - which
    ///     will fail on the consumed hold, correctly, because by then the
    ///     booking is long settled and a client still retrying is not
    ///     recovering from a dropped connection.
    ///     <para>
    ///         Short on purpose: <see cref="ManagementToken"/> is a live
    ///         credential held in plaintext, so this window is the whole
    ///         duration of that exposure. A day comfortably covers a client
    ///         retrying across an outage, a backgrounded mobile app, or a
    ///         person reloading a checkout tab, and covers nothing else.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    /// <summary>
    ///     The pre-generated booking id, exactly as
    ///     <see cref="PendingBookingIntent.Id"/> is - so the reconcile job and
    ///     the compensating paths can resolve this row without a second
    ///     lookup, using the id they already hold.
    /// </summary>
    public Guid BookingId { get; set; }

    /// <summary>
    ///     SHA-256 of the client's key, never the key itself. The key is a
    ///     bearer value in the weak sense: presenting it returns a management
    ///     token, so a database reader who could see stored keys could replay
    ///     other people's checkouts. Hashing costs nothing here because
    ///     lookup is by equality, not range.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    ///     SHA-256 over the request's semantic fields. A key presented with a
    ///     different payload is a client bug, and answering it with the first
    ///     request's booking would silently confirm something the caller did
    ///     not ask for.
    ///     <para>
    ///         It is also what makes a guessed key useless. Replay returns a
    ///         management token, so without this an attacker who guessed a key
    ///         would be handed control of somebody's booking; with it they
    ///         must already know the hold id and the guest's name and email,
    ///         at which point the key has told them nothing new.
    ///     </para>
    /// </summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Null while the confirmation is in flight. Set in the same
    ///     transaction that inserts the <c>Booking</c>, so it is true if and
    ///     only if there is a booking to replay - the same
    ///     present-iff-committed property the intent gained when the hold
    ///     transition joined its transaction.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    ///     The plaintext management token handed to an anonymous guest, and
    ///     the only field here that is not recoverable from committed state.
    ///     <para>
    ///         Storing it is a deliberate, bounded reversal of the
    ///         hash-only rule that governs
    ///         <see cref="BookingManagementToken"/>, and it is the entire cost
    ///         of this feature. The alternative - replay the booking but not
    ///         the token - leaves the guest exactly as locked out as before,
    ///         which is to say it does not implement the feature for the only
    ///         people who need it. The exposure is bounded by
    ///         <see cref="ReplayWindow"/>, is confined to one column, and
    ///         covers a value the client already holds in plaintext anyway.
    ///     </para>
    ///     <para>
    ///         Null for an authenticated caller: they get no management token
    ///         to begin with, because their booking is reachable through their
    ///         account. Nothing about the replay path changes for them, so
    ///         there is nothing to store.
    ///     </para>
    /// </summary>
    public string? ManagementToken { get; set; }
}
