# 0003 - Cross-module writes commit in one database transaction

**Status:** Accepted

## Context

This is a modular monolith: each module (Identity, Catalog, Hosts, Promotions, Bookings, Transactions, Reviews) owns its own `DbContext`, and all of them map one physical Postgres database. Several workflows write across modules as one logical operation:

- **Becoming a host** (`BecomeHostHandler`): registers a `Host` (Hosts) and links it to the caller's account (Identity).
- **Confirming a booking** (`ConfirmBookingHandler`): claims the hold and writes the `Booking` (Bookings), redeeming a promotion (Promotions) on the way.
- **A payment succeeding** (`MarkTransactionSucceededHandler`): marks the `Transaction` succeeded (Transactions) and confirms the `Booking` (Bookings), or refunds the payment if the booking can no longer use it.
- **Cancelling or expiring a booking**: cancels the `Booking` and releases its hold (Bookings) and reverses any redemption (Promotions); a guest cancellation also records any refund (Transactions).

Each module's writes going through its own context on its own connection made every one of these several commits. The design that answered that - compensating actions delivered by a transactional outbox, durable intent records for forward calls, and reconcile jobs over both - carried 22 distinct recovery states, and the outbox relay held a claim transaction while its handlers opened a second module's connection. At `MaxPoolSize=5`, 20 concurrent payment successes stalled for 15-46 s on pool exhaustion (docs/design/transaction-ownership.md, Stage 0).

## Decision

**A workflow that writes across modules runs as one Read Committed database transaction, on one connection, through `IAtomicScope`** ([ADR-0029](0029-atomic-scopes-for-cross-module-work.md)). The calling module's context owns the transaction; every other module named at the call site borrows its connection and transaction for the duration. The module boundary stays where [ADR-0004](0004-module-boundaries-via-contracts-projects.md) puts it: a module still writes only its own tables, through its own context and its own `*.Contracts` implementation. What changes is who owns the commit.

Consequently:

- **No compensation, outbox or intent record.** A failure anywhere rolls back every module's writes. There is no half-finished state to deliver, reconcile or detect.
- **Recovery reduces to the retry protocol** ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)): the one ambiguity left is a commit whose acknowledgement is lost, and each workflow recognises its own committed attempt by an identity minted before the scope.
- **Lock order is derived over the merged scope** ([ADR-0028](0028-advisory-locks-and-lock-order.md)), because every lock a workflow takes is now held until its single commit.

### What one transaction cannot cover: external facts

The scope makes *database* writes atomic with each other. It cannot make a fact outside the database atomic with them:

- **A payment succeeding is a fact reported by the gateway**, not a request that can be refused. It is recorded, never rolled back as a decision: a payment that cannot become a stay is refunded, in the same commit, rather than rejected.
- **A refund is recorded as a decision** (`RefundPending`, and the obligation's `ResolvedAt`) inside the scope. Moving money is a provider call, and a provider call must never run inside a scope: it cannot roll back, and it would hold the connection and every lock for an HTTP round trip. It runs after the commit, driven by the recorded decision.
- **The refund obligation stays** ([ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md)). A payment can succeed after the cancellation that owes it committed; the obligation is how that later scope finds what is owed.

## Alternatives considered

- **Compensating actions through a transactional outbox, with intent records for forward calls.** The previous design. It stayed correct if modules moved to separate databases, which is the property it was chosen for. Replaced because that separation is not a known requirement, and the cost was paid on every workflow now: 22 recovery states, tests that pinned dispatcher interleavings rather than outcomes, and pool starvation measured under a modest burst. Extraction remains possible and is costed in [docs/extraction-inventory.md](../extraction-inventory.md).
- **A distributed transaction (`TransactionScope`/2PC).** Rejected: there is one database, so a coordinator buys nothing a shared connection does not.
- **A messaging library (`MassTransit`, `CAP`, `Brighter`).** Rejected: broker-first, reflection-based delivery for writes that can simply share a transaction.
- **Merging the module contexts into one.** Rejected: it removes the boundary that keeps a module writing only its own tables.

## Consequences

- **Extraction is costed, not free.** Every `IAtomicScope` call site that names a module is a place that module's writes commit in another module's transaction. `docs/extraction-inventory.md` lists them, and `ExtractionInventoryProtocolTests` fails when the list and the source disagree.
- **A failure in one module fails the whole workflow.** A Transactions error during a cancellation fails the cancellation, which the caller retries, instead of cancelling and retrying the refund in the background. That is the intended trade: a caller-visible failure over a half-applied one.
- **Locks are held for the whole workflow.** A cancellation holds its booking lock across the redemption reversal and the refund decision. Stage 6 measurements show one pooled connection per converted workflow and no stall under the same burst; lock duration is what to revisit if contention on a single booking appears.
- **`HoldAvailabilityHandler` is outside this decision.** It keeps its own Serializable transaction and reads Catalog on a second connection (ADR-0029).
- Every new cross-module write is checked against this rule: it runs in a scope naming every module it touches, and nothing inside the scope calls outside the database.
