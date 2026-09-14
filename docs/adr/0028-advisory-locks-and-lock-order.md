# 0028 - Advisory locks and lock order

**Status:** Accepted

## Context

Several pairs of operations must exclude each other but share no row to lock: one side is an `INSERT` (no row exists yet), or the two sides are in different modules, and a row lock would require one module to name another's table ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)). Where several locks are taken by more than one path, their order decides whether those paths can deadlock.

## Decision

**Postgres transaction-scoped advisory locks, keyed by a scope name and an entity id** (`BuildingBlocks.Persistence.AdvisoryLock`).

- Always `pg_advisory_xact_lock`: released when the transaction ends however it ends, and useless outside a transaction, so callers take it inside one.
- `AdvisoryLock.KeyFor(scope, id)` hashes `"{scope}:{id}"` to 64 bits, so locks on different kinds of entity do not share a key space. The scope string is a wire format between deployments: renaming it while two versions run against one database makes the two stop excluding each other.
- Each lock is a named type both sides share, so they cannot disagree on the key.

| Lock | Taken by | Mode | Excludes |
|---|---|---|---|
| `BookingPaymentLock` | every path that cancels a booking (`CancelBookingHandler`, `ExpireUnpaidBookingsJob`), and `InitiateTransactionHandler` | exclusive | opening a payment against a booking being cancelled |
| `UnitAvailabilityLock` | `HoldAvailabilityHandler`, and `UnitArchival.EnsureArchivableAsync` | exclusive | a hold landing on a unit being archived; concurrent holds on one unit ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)) |
| `PropertyUnitsLock` | `CreateUnitHandler` (shared), `DeletePropertyHandler` (exclusive) | shared / exclusive | a unit being created under a property being archived; concurrent unit creation is not serialised |

### Order

**Advisory lock, then the booking row lock, then the hold.**

- Two paths take both `BookingPaymentLock` and the booking's `FOR UPDATE` row lock - cancellation and expiry - so they take them in one order. Advisory first also means a path waiting on it holds no row lock while it waits, so `BookingPaymentConfirmation`, which takes the row lock alone, never queues behind an unrelated initiation.
- Booking before hold: `BookingPaymentConfirmation` locks the booking and then marks the hold paid, so cancellation locks the booking before releasing the hold. The reverse order deadlocks against a concurrent payment; `40P01` is retried, but only by redoing the whole transaction under contention.
- Archiving a property takes `PropertyUnitsLock`, then each unit's `UnitAvailabilityLock`.

### Taking a lock is not re-reading

A lock orders two operations; it says nothing about what the other did first. Every holder re-reads the state it depends on after acquiring: initiation re-reads the booking's payability, creation re-reads the property, holds re-read the unit.

### Sweeps skip rather than wait

`ExpireUnpaidBookingsJob` takes `BookingPaymentLock` with `pg_try_advisory_xact_lock` and its row with `FOR UPDATE SKIP LOCKED`, and steps over a booking when either is held; it is found again on the next run. A blocking acquisition would put every booking behind one contended row. Both are inside one transaction, so skipping at either releases the other with the rollback.

### Checks against a moving state machine are ordered

`UnitArchival` checks for live holds before live bookings. The lock excludes new holds but does not freeze an existing one, which can move `held → pending_payment → booked` between the two checks; `HasActiveHoldForUnitAsync` excludes `booked`, and `booked` implies a confirmed booking because `MarkHoldPaidAsync` runs in the same transaction as `Booking.Confirm()`. Checked in that order, the later check covers every state the earlier one can transition into.

## Alternatives considered

- **Row locks across modules.** Rejected: Transactions would name `bookings`, Bookings would name `units`.
- **A row lock on `properties` for unit creation.** Rejected: both sides are in Catalog so it would work, but creation would take `FOR UPDATE` purely as a signal and block unrelated property edits.
- **Shared mode for holds**, so holds on one unit run in parallel. Rejected: concurrent inserts into the exclusion constraint arbitrate by deadlock ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)).
- **Session-scoped advisory locks.** Rejected: an exception path can leak one.

## Consequences

- `BookingPaymentLockProtocolTests` scans source: every file calling `Booking.Cancel` takes `BookingPaymentLock` before its row lock and before the cancel. It proves the lock is present, not that it is taken on the right transaction or in the right mode.
- The behavioural evidence is `PaymentInitiationRaceTests` (in-lock cancellation and expiry fail with the lock removed), `ArchivalRaceTests`, and `HoldExclusionConstraintTests`.
- `BookingPaymentLock` does not exclude an existing `Pending` payment succeeding: `MarkTransactionSucceededHandler` does not take it. That interleaving is compensated by the refund obligation ([ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md)).
