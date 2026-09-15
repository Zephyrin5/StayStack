# Transaction ownership refactor: Stage 1 design

Status: **for review**. No production code changes accompany this document.

Stage 0 measurements (harness in `tests/IntegrationTests/Measurements`, commit `3d4d9ae`) that shape it:

- Peak pooled connections: confirm 1; payment success, cancel and relay ticks 2. Every 2 is an outbox claim transaction held while its handler opens a second module's connection.
- 20 concurrent payment successes at `MaxPoolSize=5`: five `transactions_outbox_messages` claims sit idle in transaction for 15,019 ms each, the full Npgsql acquisition timeout, then fail and retry. Requests still return 200 because inline dispatch swallows handler failures.
- 20 concurrent confirms at `MaxPoolSize=5` during relay: complete in 123 ms.

## 1.1 Transaction ownership inventory

"Calls out while holding" means it resolves another module's context, and so a second pooled connection, while its own transaction is open.

| # | Site | Owning context | Isolation | Calls out while holding | Becomes |
|---|---|---|---|---|---|
| 1 | `OutboxDispatcherBase.cs:290/302` | each module's | RC | **Yes**, every handler | **Deleted** (Stage 6) |
| 2 | `ConfirmBookingHandler.cs:362/372` (`BeginConfirmationAsync`) | Bookings | RC | No (Promotions is called between its transactions) | **Scope owner** (Stage 4): Bookings, Promotions |
| 3 | `PromotionRedemption.cs:72` (`RedeemAsync`) | Promotions | RC | No | **Participant** (Stage 4) |
| 4 | `PromotionRedemption.cs:202` (`ReverseRedemptionAsync`) | Promotions | RC | No; runs inside an outbox claim today | **Participant** (Stages 4-5) |
| 5 | `CancelBookingHandler.cs:97/104` | Bookings | RC | No; dispatches after commit | **Scope owner** (Stage 5): Bookings, Transactions, Promotions |
| 6 | `ExpireUnpaidBookingsJob.cs:99/106` | Bookings | RC | **Yes**: `ITransactionLookup.HasSucceededPaymentAsync` under the row lock | **Scope owner** (Stage 5): Bookings, Transactions, Promotions |
| 7 | `BookingPaymentConfirmation.cs:61/68` | Bookings | RC | No | **Participant** (Stage 5) |
| 8 | `ReconcileOrphanedBookingIntentsJob.cs:127/134` | Bookings | RC | No; dispatches after | **Deleted** (Stage 4) |
| 9 | `ReconcileOrphanedHostLinkIntentsJob.cs:134/141` | Identity | RC | No; dispatches after | **Deleted** (Stage 3) |
| 10 | `HoldAvailabilityHandler.cs:102/114` | Bookings | **Serializable** | **Yes**: `IUnitLookup.GetUnitAsync` re-read under the unit lock | **Keeps ownership** (invariant). See D4. |
| 11 | `InitiateTransactionHandler.cs:52/65` | Transactions | RC | **Yes**: `IBookingLookup.GetBookingDetailsAsync` under `BookingPaymentLock` | Keeps ownership; see D4 |
| 12 | `DeleteUnitHandler.cs:55/70` | Catalog | RC | **Yes**: archival guard and availability reads under `UnitAvailabilityLock` | Keeps ownership; see D4 |
| 13 | `DeletePropertyHandler.cs:55/65` | Catalog | RC | **Yes**, same, per unit | Keeps ownership; see D4 |
| 14 | `CreateUnitHandler.cs:72/79` | Catalog | RC | No | Keeps ownership |
| 15 | `CreatePricingRuleHandler.cs:56/68` | Catalog | RC | No | Keeps ownership |
| 16 | `UpdatePricingRuleHandler.cs:76/83` | Catalog | RC | No | Keeps ownership |
| 17 | `RefreshTokenHandler.cs:33/55` | Identity | RC | No | Keeps ownership |
| 18 | `SignUpHandler.cs:41/53` | Identity | RC | No | Keeps ownership |
| 19 | `HoldConfirmation.cs:41` (ambient) and `UnitArchival.cs:49` (ambient) | caller's | - | - | **Unchanged.** Verified: after `Database.UseTransaction`, `CurrentTransaction.GetDbTransaction()` is the scope's own `NpgsqlTransaction` (probe, see 1.2) |

Writes with no explicit transaction that join a scope:

| Site | Today | Becomes |
|---|---|---|
| `BecomeHostHandler` (intent insert, `HostRegistrar.RegisterHostAsync`, `UserManager.UpdateAsync`, `AddToRoleAsync`, refresh token) | five independent commits | **Scope owner** (Stage 3): Identity, Hosts |
| `HostRegistrar.RegisterHostAsync` | bare save with primary-key recovery | **Participant** (Stage 3) |
| `MarkTransactionSucceededHandler` | save plus outbox row, then dispatch | **Scope owner** (Stage 5): Transactions, Bookings |
| `TransactionReversal.ResolveAsync` | saves Transactions, then `MarkRefundObligationResolvedAsync` on Bookings | **Participant** in cancel, expiry and payment scopes; **scope owner** in `ResolveOutstandingRefundsJob` (D5) |

### Recovery states the system maintains

Today, 22 distinct states:

| Workflow | States |
|---|---|
| Confirm | (1) hold `pending_payment` + intent, no redemption, no booking; (2) the same with a committed redemption; (3) compensation rows saved, intent not yet discarded; (4) intent reconciled while the request is in flight; (5) booking committed, acknowledgement lost; (6) idempotency record reserved but incomplete; (7) `hold_id` collision in the compensation gap; (8) `key_hash` collision |
| Outbox (all) | (9) row pending or backing off; (10) dead-lettered; (11) handler's cross-module effect committed, claim rolled back |
| Become host | (12) intent only; (13) intent + Host; (14) intent + Host + link, no role; (15) `DeleteHost` queued; (16) unlink failed, left to the job |
| Cancel | (17) cancelled + obligation, dispatches pending; (18) refund recorded, marker unset; (19) marker set, no refund |
| Payment | (20) transaction Succeeded, booking Pending; (21) dead-lettered confirmation resolved to a refund |
| Expiry | (22) cancelled, redemption reversal pending |

After Stages 3-6, 5:

| State | Why it survives |
|---|---|
| Each workflow's own committed-but-unacknowledged attempt (confirm, become host, cancel, payment) | The execution strategy retries after a lost acknowledgement; atomicity does not remove that |
| A payment succeeding after a cancellation or expiry committed, resolved from a durable `RefundObligation` | External payment truth: money taken cannot be rolled back (ADR-0027) |

`key_hash` replay (8) remains, as a feature rather than a recovery state.

### Independently committed steps per workflow

| Workflow | Today | After |
|---|---|---|
| Confirm (with a code) | 3 on success (transaction A, redemption, final save); up to 6 on failure (plus compensation save and two dispatch claims) | 1 |
| Become host | 5 | 1 |
| Cancel (paid, with a code) | 6 (cancel; claim + refund + marker; claim + reversal) | 1 |
| Payment success | 3 (save + row; claim; booking confirmation), 5 if refunded | 1 |
| Expiry | 3 (expire; claim + reversal) | 1 |

## 1.2 `IAtomicScope` contract

Interface in `BuildingBlocks/Persistence` with no EF types; implementation in `Infrastructure/Persistence`.

```csharp
/// <summary>
///     Runs one unit of work across several modules' contexts as a single database
///     transaction on a single pooled connection.
///
///     OWNERSHIP. The scope owns the transaction and the retry loop. It opens both
///     on the first participant (the calling module's context): that context's
///     execution strategy runs the work, and that context's connection and
///     transaction are the only ones used. Every other participant borrows them.
///
///     PARTICIPATION IS EXPLICIT. The caller lists every participant. A participant
///     is an IAtomicParticipant resolved by module key ([FromKeyedServices]), so a
///     handler names the modules its workflow writes to without referencing their
///     DbContext types. A context the caller did not list is not enlisted: it keeps
///     its own connection, and an attempt to write through it from inside the work
///     is a bug the scope detects at commit (see VERIFIED STATE).
///
///     PARTICIPANTS DO NOT OWN TRANSACTIONS. A method that may run inside a scope
///     never calls BeginTransactionAsync, CommitAsync or RollbackAsync. It uses the
///     ambient transaction, and requires one (HoldConfirmation.RequiredTransaction is
///     the precedent). After a database error it rethrows: Postgres aborts the
///     transaction on a failed statement, so nothing further can run in it, and the
///     scope rolls back.
///
///     ENLISTMENT. Each attempt, for every participant in order:
///       1. assert its connection is closed and it has no CurrentTransaction;
///       2. ChangeTracker.Clear();
///       3. for the owner, BeginTransactionAsync(ReadCommitted);
///          for the others, SetDbConnection(owner's connection, contextOwnsConnection:
///          false), then UseTransaction(owner's DbTransaction).
///     Contexts constructed before the scope opened are enlisted the same way: EF
///     opens and closes a context's connection per operation, so an idle scoped
///     context is closed and can be re-pointed.
///
///     RETRIES. The work delegate is the unit of retry. Every attempt re-enlists and
///     clears every participant's change tracker, not only the owner's, because a
///     rolled-back attempt leaves tracked entities describing writes that never
///     committed. Identity a retry must recognise is minted by the caller before
///     calling ExecuteAsync (docs/adr/0025). Nested context operations do not retry
///     on their own: EF suspends execution strategies inside a running one.
///
///     DAPPER. A participant passes Database.CurrentTransaction.GetDbTransaction() and
///     uses Database.GetDbConnection(); both are the owner's.
///
///     COMMIT. The work saves through each participant itself. Before committing, the
///     scope asserts no participant has unsaved changes, so a forgotten save fails
///     loudly instead of silently dropping writes.
///
///     RELEASE, on every path (commit, exception, cancellation):
///       - for non-owners: UseTransaction(null), SetDbConnection(null),
///         SetConnectionString(the participant's original connection string);
///       - for the owner: the transaction is disposed, and EF closes the connection
///         it opened, returning it to the pool.
///     The owner's context owns the connection; the scope never disposes it.
///
///     NOT SUPPORTED: nesting a scope inside a scope (throws); isolation other than
///     Read Committed (HoldAvailabilityHandler keeps its own Serializable
///     transaction); lazily enlisting a context the caller did not list.
/// </summary>
public interface IAtomicScope
{
    Task<T> ExecuteAsync<T>(
        IReadOnlyList<IAtomicParticipant> participants,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

/// <summary>Opaque. Registered per module under an AtomicParticipants key.</summary>
public interface IAtomicParticipant;
```

**Verified before writing this** with a throwaway probe against real Postgres and two scoped contexts already used once:

- `SetDbConnection` on the idle context succeeds.
- After `UseTransaction`, the context's `CurrentTransaction.GetDbTransaction()` is reference-equal to the owner's transaction. So `HoldConfirmation` and `UnitArchival` work unchanged.
- Dapper and EF commands on the participant run inside the owner's `CreateExecutionStrategy().ExecuteAsync`, with no user-initiated-transaction error.
- The owner reads the participant's uncommitted write.
- A transient failure on attempt 1 re-enlists cleanly on attempt 2.
- Release gap: `SetDbConnection(null)` alone leaves no connection string and the next query throws. Adding `SetConnectionString(original)` restores a working context on its own pooled connection. The contract's release step is written from that result.

Stage 2's rollback and retry tests are the lasting proof; the probe was deleted.

## Deviations from the brief, for decision

**D1. No second context registration.** The brief proposes a keyed or explicitly constructed registration, and a `ConfigureStayStackDefaults` overload taking a connection. Contracts implementations (`PromotionRedemption`, `HostRegistrar`, `TransactionReversal`, `BookingLookup`) receive their module's default scoped context. A second registration would give one request two instances per module, and the Contracts implementation would write through the wrong one. Enlisting the existing scoped instance for the scope's duration (verified above) keeps one instance per module. The global registration is untouched, so unconverted paths keep their own connections, which is the brief's actual constraint.

**D2. Interface and implementation split.** `BuildingBlocks` has no EF reference, by design. The interface lives there; the implementation, `IAtomicParticipant` adapters and registration helpers live in `Infrastructure/Persistence`.

**D3. Idempotency record lifecycle becomes simpler, but the code stays.** With the record inserted in the same transaction as the booking, a record exists if and only if its booking does. `CompletedAt == null` and ReplayAsync's "still in progress" 409 become unreachable. The brief keeps `CheckoutIdempotencyRecord` unchanged, so Stage 4 leaves both in place and records the unreachability in a comment rather than removing them.

**D4. Read-only nested acquisitions outside the brief's list.** Four owners open a second module's connection while holding a transaction and a lock, for reads: `HoldAvailabilityHandler`, `InitiateTransactionHandler`, `DeleteUnitHandler`, `DeletePropertyHandler`. They are the same pool-exhaustion shape Stage 0 measured, at lower frequency.
- **Recommendation:** convert `InitiateTransactionHandler` (owner Transactions, participant Bookings) and the two archival handlers (owner Catalog, participant Bookings) into scopes in Stage 5.
- **`HoldAvailabilityHandler` cannot convert** without violating the Serializable invariant. Its `IUnitLookup` re-read stays nested; I propose recording it as a known residual in the new ADR.
- **Decision needed:** include the three conversions, or leave all four for a later piece of work.

**D5. `ResolveOutstandingRefundsJob` as a scope.** With the refund and the obligation's `ResolvedAt` in one transaction, ADR-0027's marker/refund asymmetry (states 18 and 19) disappears. `ResolvedAt` keeps its meaning: the decision is recorded locally, not that money moved. Recommended; it is what takes the recovery-state count to 5.

**D6. Payment success must lock the booking before the transaction row.**
- **Merged, the orders cross.** Cancel takes `BookingPaymentLock`, then the booking `FOR UPDATE`, then the hold, then refund resolution (which updates the transaction row). Payment success in one scope would take the transaction row first (`MarkSucceeded`), then the booking. Those deadlock.
- **Proposed order.** The payment scope reads the transaction, calls `ConfirmPaymentAsync` (booking row lock, then hold), and then saves `MarkSucceeded`, so both paths lock booking before transaction. A concurrent finalization still surfaces as the transaction's xmin conflict and rolls the whole scope back, giving the same 409.
- **ADR-0028** gains the re-derivation in Stage 5.

**D7. `RedeemAsync`'s own committed-attempt lookup becomes dead.** Its redemption id is minted inside the call. Inside a scope, a retry after a rollback mints a new id against a clean database; after a lost outer acknowledgement, recovery happens before `RedeemAsync` is reached, by the pre-generated booking id. Stage 4 deletes the lookup.

## Open risk carried into Stage 2

`CommitFaults` hooks `DbTransactionInterceptor` on the context's own `IDbContextTransaction`. The owner's commit is still an EF transaction commit (`BeginTransactionAsync` on the owner), so the hook should fire. Stage 2 verifies this explicitly and reports either way.
