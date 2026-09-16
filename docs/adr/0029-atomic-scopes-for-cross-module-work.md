# 0029 - Atomic scopes for cross-module work

**Status:** Accepted

## Context

[ADR-0003](0003-cross-module-writes-commit-in-one-transaction.md) decides that a cross-module workflow commits as one database transaction. Each module's `DbContext` is registered scoped with `AddDbContext`, configured with `EnableRetryOnFailure`, and reached from other modules only through its `*.Contracts` implementations, which receive that scoped instance. The mechanism has to make those existing instances share one connection and transaction without letting any module name another's context, and without changing how a module's own code writes.

## Decision

**`IAtomicScope`** (`BuildingBlocks.Persistence`, no EF types) with its implementation in `Infrastructure/Persistence`:

```csharp
Task<T> ExecuteAsync<T>(
    AtomicParticipants owner,
    AtomicParticipants participants,
    Func<CancellationToken, Task<T>> work,
    CancellationToken cancellationToken);
```

### Participation is explicit, at the call site

`AtomicParticipants` is a flags enum with one value per module. The caller names the owner - one flag, its own module - and every participant. Each module registers its context against its flag (`AddAtomicParticipant<TContext>`). A context the caller did not name is not enlisted and keeps its own connection. Participation is therefore readable and greppable at every call site, which is what the extraction inventory is built from.

The owner is a separate argument rather than the first flag of a union: it decides whose execution strategy retries, whose connection is used, and which context commit-fault injection must target, so it has to be unambiguous.

### The existing scoped contexts are re-pointed

No second registration. For the scope's duration each non-owner participant's existing scoped instance is given the owner's connection (`SetDbConnection(connection, contextOwnsConnection: false)`) and enlisted in its transaction (`UseTransactionAsync`). A second, keyed registration would give one request two instances per module, and a Contracts implementation would write through the wrong one.

- **Entry:** every participant must be idle - no open connection, no transaction, no unsaved changes. A participant that is not throws naming its context, instead of failing later inside EF. This also refuses a scope nested over the same context.
- **Release, on every path including failure and cancellation:** each borrower leaves the transaction, drops the borrowed connection, and gets its own connection string back (`SetDbConnection(null)` alone leaves the context unable to run its next query). When the scope does not commit, every participant's change tracker is cleared: entities saved and rolled back read as `Unchanged` but describe rows that do not exist.
- **Pooled contexts cannot participate**, since a context returned to the pool mid-lend would carry the borrowed connection into another request. `DbContextRegistrationProtocolTests` fails on `AddDbContextPool` or `AddPooledDbContextFactory`.

### The work is the unit of retry

The owner's execution strategy runs the work. Each attempt clears every participant's tracker, begins the owner's transaction and re-enlists the borrowers. Operations on participants inside the work do not retry on their own: EF suspends execution strategies inside a running one. Identity a retry must recognise is minted before `ExecuteAsync` ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)); `RetryIdentityProtocolTests` scans scope delegates as retry delegates.

### Participants do not own transactions

A Contracts method that runs inside a scope never begins, commits or rolls back. It requires the ambient transaction and throws without one: `HoldConfirmation`, `PromotionRedemption.RedeemAsync` and `ReverseRedemptionAsync`, `BookingPaymentConfirmation`, `TransactionReversal`. `HostRegistrar.RegisterHostAsync` does not check, and relies on its only caller, `BecomeHostHandler`, running it in a scope. Dapper statements use the participant's `Database.GetDbConnection()` and `CurrentTransaction.GetDbTransaction()`, both the owner's. Before committing, the scope throws if any participant has unsaved changes.

### Nothing inside a scope leaves the database

No HTTP call, no payment-provider call, no message publish. Such an effect cannot roll back with the scope and would hold its connection and locks for its duration. `ResolveOutstandingRefundsJob` states this at its call site.

### Scopes in use

The call sites: become host, confirm, cancel, expiry, payment success, the refund sweep, and three read-only participations - initiation, unit archival and property archival - where the other module is only read under a lock, so the read uses the owner's connection instead of a second one.

### `HoldAvailabilityHandler` stays outside, deliberately

It runs a Serializable transaction and re-reads the unit through Catalog under the unit lock, on Catalog's own connection. The scope could take an isolation-level parameter and enlist Catalog, but that would widen the Serializable envelope over a cross-module read and change the serialization-failure retry characteristics of the most contended path in the system. A hold therefore still uses two pooled connections; the Stage 6 measurement shows it as the one operation with a peak of 2.

## Alternatives considered

- **Lazy enlistment** - a context joins whatever scope is ambient when first used. Rejected: participation becomes invisible at the call site, and a Contracts call added later silently joins or silently does not.
- **An ambient static (`AsyncLocal`) transaction.** Rejected for the same reason, and because every data access would have to consult it.
- **A generic unit of work over all contexts.** Rejected: it enlists every module on every operation, and makes "which modules does this workflow write" unanswerable from the code.
- **A second, keyed context registration for scoped work.** Rejected above: two instances per module per request.
- **`TransactionScope`.** Rejected: ambient, and it coordinates separate connections through System.Transactions rather than sharing one.

## Consequences

- `AtomicScopeTests` pins rollback across EF and Dapper participants, retry without duplicates or stale entities, the idle-entry and unsaved-changes refusals, restored connections, and that `CommitFaults` fire on the owner's commit and not a borrower's. The release step and each tracker-clearing step were verified by removing them from the implementation; the entry refusals were not.
- **Failure injection targets the owner.** A borrower never commits, so a commit fault registered on a borrower's context never fires.
- **Adding a scope, or a participant to one, is a documentation change too** - `ExtractionInventoryProtocolTests` fails until the inventory matches.
- **Measured:** peak connections per converted operation 2 → 1; 20 concurrent payment successes at `MaxPoolSize=5` from 15-46 s stalls with failed dispatches to ~0.3 s, all succeeding.
