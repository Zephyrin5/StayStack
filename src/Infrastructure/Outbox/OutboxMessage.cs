namespace Outbox;

/// <summary>
///     A durable record of "please eventually do X in another module",
///     written atomically (same SaveChangesAsync call) alongside whatever
///     local write it follows from - see docs/adr/0003. Not Entity-derived:
///     this is a delivery mechanism, not a domain concept, so the
///     CreatedBy/ModifiedBy/Status audit shape Entity carries for every
///     business aggregate doesn't apply here.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; init; }

    // The message's own CLR type name (e.g. "ReleaseHoldOutboxMessage") -
    // each module's dispatcher switches on this to know which JsonTypeInfo
    // to deserialize Payload with and which Contracts call to make.
    public required string Type { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // Defaults to CreatedAt so a fresh row is eligible on the relay job's
    // very next poll - only pushed forward once a dispatch attempt fails.
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    // Set once Attempts reaches OutboxDispatcherBase.MaxAttempts. Not
    // auto-recovered - the same accepted-risk-window shape ADR-0003 already
    // documents for ReconcileOrphanedBookedHoldsJob's own lookback cap. See
    // ADR-0003's Consequences for what's actually stuck behind each message
    // type once this is set.
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
