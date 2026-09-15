# 0003 - Compensating actions and a transactional outbox for cross-module writes

**Status:** Accepted. The forward half of a cross-module write is covered by [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md).

## Context

This is a modular monolith: each module (Identity, Catalog, Hosts, Promotions, Bookings, Transactions, Reviews) owns its own `DbContext` and, conceptually, its own database, though all share one physical Postgres instance. Several workflows write across modules as one logical operation:

- **Becoming a host** (`BecomeHostHandler`): registers a `Host` (Hosts) and links it to the caller's account (Identity).
- **Confirming a booking** (`ConfirmBookingHandler`): redeems a promotion (Promotions) and writes the `Booking` (Bookings).
- **A payment succeeding** (`MarkTransactionSucceededHandler`): marks the `Transaction` succeeded (Transactions) and confirms the `Booking` (Bookings), or refunds the payment if the booking is already cancelled.
- **Cancelling or expiring a booking**: cancels the `Booking` (Bookings), resolves any refund (Transactions), and reverses any redemption (Promotions).

These span `DbContext`s and connections, so none can be one ACID transaction.

## Decision

**Two shapes, chosen per step.**

**(a) A forward call whose result the current write needs synchronously stays a direct call** - `RedeemAsync`'s discount, `RegisterHostAsync` under a caller-supplied host id. Writes are ordered so the more authoritative fact commits first: `MarkTransactionSucceededHandler` commits `Succeeded` before anything booking-side happens, because "the payment succeeded" stays true whatever follows. A forward call is recorded by a durable intent before it runs ([ADR-0017](0017-durable-intent-records-for-cross-module-writes.md)).

**(b) A follow-up that runs after a local write is durable goes through a transactional outbox** - including every compensation for a forward call. The outbox row is written in the same `SaveChangesAsync` as the state change it accompanies (`booking.Cancel()`, `transaction.MarkSucceeded()`), so neither exists without the other. Current message types:

| Module | Message | Written by |
|---|---|---|
| Bookings | `ReverseTransactionOutboxMessage` | `CancelBookingHandler` |
| Bookings | `ReverseRedemptionOutboxMessage` | `CancelBookingHandler`, `ExpireUnpaidBookingsJob`, `ConfirmBookingHandler`'s compensation, `ReconcileOrphanedBookingIntentsJob` |
| Bookings | `ReleaseHoldOutboxMessage` | `ConfirmBookingHandler`'s compensation |
| Transactions | `ConfirmBookingPaymentOutboxMessage` | `MarkTransactionSucceededHandler` |
| Identity | `DeleteHostOutboxMessage` | `BecomeHostHandler`'s compensation, `ReconcileOrphanedHostLinkIntentsJob` |

**Delivery.** `OutboxDispatcherBase<TDbContext>`, one dispatcher and one TickerQ relay job per owning module:

- A row is dispatched **inline immediately after the commit that wrote it**; the relay (`* * * * *`) delivers whatever the inline attempt did not finish. Happy-path latency matches a direct call.
- Each attempt claims the row with `FOR UPDATE SKIP LOCKED` inside a transaction that also records the outcome, and **the handler runs inside that transaction**. A handler's writes through the module's own context commit with the claim; its calls into other modules commit independently and immediately. So a cross-module effect can happen while the claim rolls back, and every handler must tolerate being re-run after partial effect.
- Failures back off (30s, 1m, 5m, 15m, 1h) up to 10 attempts, then the row is dead-lettered: an `Error` log, `OutboxTelemetry.DeadLettered`, and a module's `OnDeadLetteredAsync` hook, once per row that newly crosses the threshold. Telemetry fires after the execution strategy commits, so a retried attempt does not count twice; the hook runs inside, because its writes must commit with the row.
- **Dead-lettered is not abandoned.** An hourly `SweepDeadLetteredAsync` replays each dead-lettered row from its stored payload. Replaying the payload is safer than re-deriving the outcome from current state, because the value was computed once and stored. A renewed failure re-dead-letters without re-counting (`OutboxTelemetry.DeadLetterRetried` and a `Warning` record each retry).
- Processed rows are purged after 30 days; they are the only record that a compensation was dispatched.

**Handlers verify state instead of applying deltas.** `ReleaseHoldAsync` matches only `pending_payment` or `booked`; `ReverseRedemptionAsync` matches `reversed_at IS NULL`; `DeleteAsync` no-ops on a missing host; refunds are decided by the idempotent resolver ([ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md)). `TransactionsOutboxDispatcher.OnDeadLetteredAsync` checks whether the booking is already confirmed before refunding, because a dead-lettered confirmation can be a lost acknowledgement rather than lost work. `OutboxIdempotencyTests` demonstrates a re-run after partial effect.

**Structure.**

- `src/Infrastructure/Outbox` is its own project with no module dependency, referenced by the modules that own messages.
- Tables are module-prefixed (`bookings_outbox_messages`, `transactions_outbox_messages`, `identity_outbox_messages`), because all modules share one physical schema.
- Dispatch uses no reflection: `TryHandleAsync` is a hand-written `switch` over `OutboxMessage.Type`, and payloads go through each module's source-generated `JsonSerializerContext`. `OutboxTypeDiscriminatorTests` pins the type names, which are persisted.

## Alternatives considered

- **Distributed transaction (`TransactionScope`/2PC) across the contexts.** Rejected as disproportionate: a coordinator and cross-database transaction machinery for a project at this scale.
- **One EF transaction shared across module contexts**, since they share an instance. Rejected: atomic only while the modules stay co-located, which defeats the extractability [ADR-0004](0004-module-boundaries-via-contracts-projects.md)'s boundaries exist for. An outbox stays correct if they separate.
- **Direct compensating calls without an outbox.** Rejected: a compensation that fails, or a process that dies before it runs, is lost with nothing to retry.
- **A messaging library (`MassTransit`, `CAP`, `Brighter`).** Rejected: broker-first and reflection-based, for reliable in-process delivery over one Postgres instance.
- **`ZeroAlloc.Outbox`.** The right shape - source-generated, no reflection, retry and dead-letter built in - but immature, and it brings a parallel `ZeroAlloc.*` ecosystem including its own mediator. Hand-rolled, the outbox is a few files per module. Worth revisiting if a library in this space builds a track record.
- **Outboxing forward calls too.** Rejected: their results are needed synchronously by the write in progress.
- **A reconciliation job per message type that re-derives the correct outcome.** Rejected in favour of replaying the stored payload.

## Consequences

- A message whose failure is not transient retries hourly until fixed; each attempt is visible, and the next sweep after a fix succeeds with no manual replay. A persistently failing message still needs someone to notice the log and counter.
- Bookings and Transactions reference each other's contracts; the outbox does not add that edge.
- Every new cross-module write is checked against this rule: does the current write need data back from the call (a direct call, recorded by an intent), or does it follow a durable local write (the outbox)? Authoritative-write-first ordering and state-verifying handlers apply either way.
