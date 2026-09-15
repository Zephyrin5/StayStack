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
///         Written in the same atomic scope as the booking, keyed by its
///         pre-generated id. It outlives the checkout because it carries the
///         one thing that exists nowhere else once the response is lost.
///     </para>
///     <para>
///         It stores no credential. Replay mints a fresh management token and
///         persists only its hash, exactly as the original checkout did, so
///         this table cannot produce a working one - see
///         ConfirmBookingHandler.ReplayAsync.
///     </para>
///     <para>
///         How long it stays replayable is
///         BookingLifecyclePolicyOptions.CheckoutReplayWindowHours, not a
///         constant here. The request path enforces it and
///         PurgeReplayedCheckoutsJob cleans up behind it, so a value two
///         things must agree on belongs where both can read it and startup can
///         validate it - the same drift the duplicated MaxLeadTimeDays
///         constants had before StaySearchPolicyOptions consolidated them.
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
