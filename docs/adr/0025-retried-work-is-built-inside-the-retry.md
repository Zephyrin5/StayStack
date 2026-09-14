# 0025 - The retry protocol

**Status:** Accepted

Owns how work behaves under the execution strategy's retries. Cross-module recovery markers are [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md); refund resolution is [ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md); lock order is [ADR-0028](0028-advisory-locks-and-lock-order.md).

## Context

Every module's `DbContext` is configured with `EnableRetryOnFailure`, including `40P01` (deadlock) and `40001` (serialization failure). The execution strategy re-runs a delegate - or a bare `SaveChangesAsync`, which it wraps too - after any transient failure. It cannot distinguish two outcomes that need opposite handling:

- **The commit failed.** Nothing was written; the retry must write it.
- **The commit landed and its acknowledgement was lost.** Everything was written; the retry meets a database its own first attempt already changed.

A handler correct for only the first reports failure, or a false conflict, for work that succeeded. A handler correct for only the second loses work silently.

## Decision

### 1. Retried work is built inside the retry

- **Construct the entities and outbox rows a delegate saves inside that delegate.** `SaveChangesAsync` accepts its changes when it returns, before `CommitAsync` runs. Work built outside and saved again on a retry is seen as unchanged: the save writes nothing, the commit succeeds, and the handler reports success over rows that were never written.
- **Clear the change tracker at the top of the delegate**, so an attempt never inherits a previous attempt's accepted or tracked entities. `Add` of a fresh instance is re-inserted after `Clear()`; a *loaded* instance mutated again is not.
- **Reload and re-lock state inside the delegate**, and build the response from the reloaded entity. An instance read before the transaction is stale on the first attempt and may describe a world a previous attempt changed.
- **Only the owner of a transaction clears its tracker.** A component running inside someone else's transaction - `TransactionReversal` inside an outbox dispatcher - shares that context, and `Clear()` there detaches the caller's entities: the dispatcher's `ProcessedAt` would go to a detached message and never save. Such a component discards the one entity it needs to (`Entry(entity).ReloadAsync()`).
- **Compensation that crosses a module is an outbox row committed with the local decision that authorises it**, dispatched after the commit ([ADR-0003](0003-compensating-actions-over-distributed-transactions.md)). A direct cross-module call inside a local transaction commits on its own connection and survives that transaction's rollback.

### 2. A retry recognises its own committed work

- **Identity is minted outside the retried scope.** Every entity factory takes a caller-supplied `Guid id`; the creating handler mints it on the first line of `Handle`. The same applies to any identity a retry must recognise that is not an entity id - `IssuedRefreshToken` carries a replacement refresh token's id and plaintext, chosen before the retry. `Entity.SetCreated` deliberately does not assign `Id`: the audit interceptor runs inside every retried delegate.
- **A write creating a row under a caller-known identity recognises a collision on that identity as its own committed attempt** and returns that row. Two shapes:
  - With an explicit delegate, look for the row by id first, before any check that would judge the request against a table already holding it - `CreateUnitHandler`, `CreatePricingRuleHandler`, `InitiateTransactionHandler`, `HoldAvailabilityHandler`, `PromotionRedemption`. Ordering matters: the hold-cap count, the pricing overlap check and the active-transaction check each reject the request's own row if they run first.
  - With a bare `SaveChangesAsync`, EF sends a single-row insert with no transaction; a lost acknowledgement retries into the primary key. Catch a violation of the entity's own primary key and read the row back through `Persistence.CommittedInsertRecovery` - `CreateProperty`, `AdminCreateProperty`, `CreateHost`, the promotion and review creates, `HostRegistrar`, `BecomeHost`'s intent.
- **Consume-once operations recover by the identity of what they produce.** A refresh consumes the presented token, so a retry after a lost acknowledgement finds it consumed - indistinguishable, by the token, from reuse. `RefreshTokenHandler` asks `FindCommittedRotationAsync` whether the token was already replaced by exactly this request's replacement id, before consuming it and before the commit-on-catch makes a family revocation durable. A replayed token arrives as a new request with a new id and still reaches reuse detection.
- **Nothing observable depends on an uncommitted write.** A transaction's outcome is decided before anything leaves the handler. `ConfirmBookingHandler` returns its replay *decision* from the retried delegate and mints a management token only after the transaction is resolved; minted inside, a rollback would hand the guest a credential whose hash row does not exist.

### 3. Constraint violations are matched by name

Constraints on one table raise the same SQLSTATE with different meanings: a primary key colliding with this operation's own committed row, a unique index held by another row, an index whose collision is transient. `Persistence.ConstraintViolations` is the only code that inspects a violation (`IsViolationOf`, `IsViolationOfAny`, `IsPrimaryKeyViolationOf`); a violation of any constraint a catch does not name propagates. Names come from the EF model for primary keys, and from constants the entity configuration or migration also uses for indexes and raw-SQL constraints.

Primary-key recovery depends on two database facts: the key carries the model's name, and it is the table's oldest unique index. A re-inserted row violates the primary key and every other unique index at once, and Postgres reports the first index it checks, in creation order; a unique index older than the key would be reported instead, and recovery would answer a domain error for the operation's own row.

## Alternatives considered

- **Idempotent writes instead of recognition.** Rejected as a general answer: cap increments, redemptions and consumed tokens are not idempotent, and an idempotent compensation that runs when the forward work succeeded is exactly the damage.
- **Matching violations by SQLSTATE and re-reading.** Rejected: it gives one answer to constraints with different meanings, and a new unique index on a table silently changes what an existing catch does.
- **Minting in the audit interceptor on save.** Rejected: it runs inside every retried delegate, so every entity would get a new identity per attempt.
- **A delegate-body-only scan for minting.** Rejected as the guard: a mint reached through a same-file helper or a service method is invisible to it, and three of the four known violations were reached that way.

## Consequences

- **Enforced structurally** by source-scanning tests in `tests/UnitTests/Persistence/`, which prove coverage, not correctness:
  - `RetryIdentityProtocolTests` - no retried delegate mints an identity, directly or through calls it can resolve.
  - `EntityIdentityProtocolTests` - no entity mints its own identity.
  - `ConstraintViolationProtocolTests` - only `ConstraintViolations` inspects a violation.
- **Enforced behaviourally** by lost-acknowledgement tests for each recovering path (`CreationAmbiguityTests`, `CatalogRetryTests`, `CancelRetryTests`, `ConfirmRetryTests`, `RefreshTokenRetryTests`, `PromotionRedemptionRetryTests`, the create-handler tests) and by `PrimaryKeyConstraintTests`, which pins the primary-key name and ordering facts against the live schema.
- **Failure injection goes through `CommitFaults`**, whose entry points name the case: `FailBeforeCommit` (the commit fails), `FailAfterCommit` (an explicit transaction's commit lands, its acknowledgement is lost), `FailAfterAutocommit` (the same for a statement EF sent without a transaction, which never reaches a commit hook). A hook on the wrong side of a commit proves the other case; `FaultInjectionProtocolTests` fails any integration test that names a transaction or command interceptor directly.
- **Faults are targeted at the commit under test.** Test hosts run TickerQ, whose jobs commit on their own schedule and would otherwise take the injection while the test still passes.
- **Committed state is asserted through a fresh scope.** A context that accepted its changes and failed to commit reads exactly like success.
