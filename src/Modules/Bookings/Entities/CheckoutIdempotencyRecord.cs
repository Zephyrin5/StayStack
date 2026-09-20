namespace Bookings.Entities;

/// <summary>
///     Lets a client retry <c>POST /api/bookings</c> after a lost acknowledgement and receive the
///     original answer instead of a second booking or a dead end.
///     <para>
///         The dead end is the reason this exists: the plaintext management token is returned in the
///         response and only its hash is persisted, so a dropped connection leaves an anonymous guest
///         with a booking they cannot reach and never learned the id of. Retrying does not help - the
///         hold has moved to 'pending_payment' and the retry fails ConfirmHoldAsync's guard.
///     </para>
///     <para>
///         It stores no credential: a replay mints a fresh token and persists only its hash, exactly
///         as the original checkout did. Written in the booking's own transaction, keyed by its
///         pre-generated id, and replayable for BookingLifecyclePolicyOptions.CheckoutReplayWindowHours.
///     </para>
/// </summary>
public sealed class CheckoutIdempotencyRecord
{
    /// <summary>
    ///     The pre-generated id of the booking this record replays.
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

    /// <summary>
    ///     When the confirmation wrote the record, in the same commit as its
    ///     <c>Booking</c>. The replay window runs from here, and the purge job
    ///     scans by it.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
