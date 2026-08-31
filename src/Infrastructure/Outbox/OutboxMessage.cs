namespace Outbox;

/// <summary>
///     A durable "please eventually do X in another module" record, written
///     atomically alongside whatever local write it follows from - see
///     docs/adr/0003. Not Entity-derived: a delivery mechanism, not a
///     domain concept, so Entity's CreatedBy/ModifiedBy/Status audit shape
///     doesn't apply.
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
    // auto-recovered - see ADR-0003's Consequences for what's stuck behind
    // each message type once this is set.
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
