# 0003 - Cross-module writes commit in one database transaction

**Status:** Accepted

## Context

This is a modular monolith over one Postgres database. Several workflows write across modules as one logical operation:

- **Becoming a host** (`BecomeHostHandler`): registers a `Host` (Hosts) and links it to the caller's account (Identity).
- **Confirming a booking** (`ConfirmBookingHandler`): claims the hold and writes the `Booking` (Bookings), redeeming a promotion (Promotions) on the way.
- **A payment succeeding** (`MarkTransactionSucceededHandler`): marks the `Transaction` succeeded (Transactions) and confirms the `Booking` (Bookings), or refunds the payment if the booking can no longer use it.
- **Cancelling or expiring a booking**: cancels the `Booking` and releases its hold (Bookings) and reverses any redemption (Promotions); a guest cancellation also records any refund (Transactions).

A module writing through a context of its own, on a connection of its own, made every one of these several commits. Holding them together needed compensating actions delivered by a transactional outbox, durable intent records for forward calls, and reconcile jobs over both: 22 distinct recovery states, and a relay that held a claim transaction while its handlers opened a second module's connection. At `MaxPoolSize=5`, 20 concurrent payment successes stalled for 15-46 s on pool exhaustion.

## Decision

**A workflow that writes across modules runs as one Read Committed database transaction, on one connection, through `ITransactionRunner`** ([ADR-0029](0029-one-context-and-the-transaction-runner.md)). There is one `AppDbContext`, assembled from the modules' model contributions; each module reaches its own tables through its own accessor (`BookingsDb`, `CatalogDb`, ...).

Consequently:

- **No compensation, outbox or intent record.** A failure anywhere rolls back every module's writes. There is no half-finished state to deliver, reconcile or detect.
- **Recovery reduces to the retry protocol** ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)): the one ambiguity left is a commit whose acknowledgement is lost, and each workflow recognises its own committed attempt by an identity minted before the runner.
- **Lock order is derived over the whole workflow** ([ADR-0028](0028-advisory-locks-and-lock-order.md)), because every lock it takes is held until its single commit.
- **A cross-module read composes rather than round-trips.** A contract may hand back an `IQueryable` the caller composes into its own query, which is one statement against one connection: stay search filters on Bookings' holds inside Catalog's search that way.

The module boundary does not rest on the context any more, so it rests on three things a test can check ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)): the project graph, the accessors, and a Postgres schema per module that its tables and its raw SQL both name. `ModuleBoundaryTests` fails on a violation of any of them.

### What one transaction cannot cover: external facts

One transaction makes *database* writes atomic with each other. It cannot make a fact outside the database atomic with them:

- **A payment succeeding is a fact reported by the gateway**, not a request that can be refused. It is recorded, never rolled back as a decision: a payment that cannot become a stay is refunded, in the same commit, rather than rejected.
- **A refund is recorded as a decision** (`RefundPending`, and the obligation's `ResolvedAt`) inside the transaction. Moving money is a provider call, and a provider call must never run inside one: it cannot roll back, and it would hold the connection and every lock for an HTTP round trip. It runs after the commit, driven by the recorded decision.
- **The refund obligation stays** ([ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md)). A payment can succeed after the cancellation that owes it committed; the obligation is how that later transaction finds what is owed.

## Alternatives considered

- **Compensating actions through a transactional outbox, with intent records for forward calls.** It stayed correct if modules moved to separate databases, which is the property it was chosen for. Replaced because that separation is not a known requirement, and the cost was paid on every workflow now: 22 recovery states, tests that pinned dispatcher interleavings rather than outcomes, and pool starvation measured under a modest burst. Extraction remains possible.
- **A context per module, sharing one connection and transaction for the duration of a workflow.** This worked and was measured, and it is what a single context replaces. Rejected because the sharing was the expensive part: entry and release checks on every participant, a flags enum naming them at each call site, a rule that pooled contexts may not participate, and an inventory test to keep the list honest - all to reproduce what one context gives for nothing. What it bought, the compiler refusing one module access to another's tables, is recovered by the project graph and per-module schemas.
- **A distributed transaction (`TransactionScope`/2PC).** Rejected: there is one database, so a coordinator buys nothing a shared connection does not.
- **A messaging library (`MassTransit`, `CAP`, `Brighter`).** Rejected: broker-first, reflection-based delivery for writes that can simply share a transaction.

## Consequences

- **Extraction is not free.** Every workflow that writes across modules commits in one transaction, so moving a module to its own database means answering for those workflows again - the cost this decision accepts in exchange for one commit per workflow.
- **A failure in one module fails the whole workflow.** A Transactions error during a cancellation fails the cancellation, which the caller retries, instead of cancelling and retrying the refund in the background. That is the intended trade: a caller-visible failure over a half-applied one.
- **Locks are held for the whole workflow.** A cancellation holds its booking lock across the redemption reversal and the refund decision. Measured: one connection per operation, holds included, and no stall under a `MaxPoolSize=5` burst. Lock duration is what to revisit if contention on a single booking appears.
- **`HoldAvailabilityHandler` runs its own transaction**, Serializable rather than Read Committed, for the reasons in [ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md) and ADR-0029.
- Every new cross-module write is checked against this rule: one transaction through the runner, and nothing inside it calls outside the database.
