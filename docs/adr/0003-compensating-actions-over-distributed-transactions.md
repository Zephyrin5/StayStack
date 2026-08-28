# 0003 - Compensating actions over distributed transactions

**Status:** Accepted

## Context

This is a modular monolith: each module (Identity, Catalog, Hosts, Bookings, Transactions) owns its own `DbContext` and, conceptually, its own database boundary, even though they currently share one physical Postgres instance. Several real workflows need to write across two modules as one logical operation:

- **Becoming a host** (`BecomeHostHandler`): registers a `Host` (Hosts module) and links it to the caller's `ApplicationUser` (Identity module).
- **Confirming a booking** (`ConfirmBookingHandler`): marks a hold `booked` (Availability) and inserts the `Booking` row (Bookings).
- **A payment resolving** (`MarkTransactionSucceededHandler`): marks a `Transaction` succeeded (Transactions) and confirms the `Booking` (Bookings) - or, if the booking was already cancelled by the time payment resolved, starts a refund instead.
- **Cancelling a booking** (`CancelBookingHandler`): cancels the `Booking` (Bookings), releases the hold (Availability), and resolves any transaction (Transactions).

None of these can be wrapped in a single ACID database transaction - they're different `DbContext`s, potentially different connections. The options are a distributed transaction coordinator (2PC), a formal outbox/reconciliation pattern, or sequential writes with hand-written compensation.

## Decision

Sequential writes with explicit, hand-written compensating actions, chosen per-workflow rather than one generic mechanism:

- Order writes so the **more authoritative fact goes first**. `MarkTransactionSucceededHandler` marks the transaction `Succeeded` before touching the booking - "the payment succeeded" is true and stays true regardless of what happens next, so it shouldn't be lost if the second write fails.
- Where the *second* write can fail in a way that leaves the *first* write's effect stranded, add an explicit compensating call: `ConfirmBookingHandler` releases the hold if the booking insert fails; `CancelBookingHandler` releases the hold and reverses any transaction after the booking's own cancellation is durable.
- Compensating actions are idempotent and safe to no-op (`ReleaseHoldAsync` only acts on a hold that's still `booked`; `ReverseTransactionAsync` only acts on a transaction that's still `Succeeded`) - retrying or racing them is never itself a source of corruption.
- If a compensating action *itself* fails, don't silently lose the original failure. `ConfirmBookingHandler` wraps both in an `AggregateException` rather than letting a bare `throw;` surface only whichever exception happened to fire last - the original reason is usually the one most needed to diagnose the resulting stuck state.
- Background sweep jobs ([ADR-0002](0002-tickerq-for-background-jobs.md)) act as a last-resort backstop for the one class of leftover state this can produce (a hold stuck `held` past its expiry), independent of *why* it was orphaned.

## Alternatives considered

- **Distributed transaction (`TransactionScope`/2PC) across the DbContexts.** Rejected as disproportionate: it adds real infrastructure complexity (a DTC or equivalent, cross-database transaction coordination) for a project at this scale, and Postgres's own cross-database distributed transaction story is not something to lean on casually.
- **Formal outbox + reconciliation job.** The more correct answer at larger scale - durably record the intent to reverse a hold/transaction, and have a separate process reconcile until it succeeds, so a compensating action can never simply fail and be left as residual state. Not adopted now: it's meaningfully more infrastructure (an outbox table, a dispatcher, idempotency keys, a reconciliation job with its own failure modes) for a codebase that doesn't yet have evidence this narrow failure window is a real operational problem. Revisit this when it does - either because compensation failures are observed happening in practice, or because the number of cross-module workflows grows enough that hand-writing a compensating action per workflow stops scaling.

## Consequences

- **A real, accepted risk window remains**: if a compensating action fails, the two modules can end up momentarily inconsistent (e.g. a hold left `booked` with no booking behind it) until a sweep job or manual intervention resolves it. This is deliberate, not an oversight - the mitigations above (idempotent compensation, exceptions preserved not swallowed, sweep jobs as a backstop) bound the blast radius without eliminating the window entirely.
- **Correction:** the "sweep job as a backstop" line above originally pointed at the wrong state. `ExpiredHoldsSweepJob` only ever looks at `status = 'held'` rows - it was never capable of catching a hold left `booked` with no booking behind it, which is exactly the leftover state a *failed* compensation (not merely an exception, one that never runs at all - a process crash between `HoldConfirmation.ConfirmHoldAsync`'s write and the `Booking` insert in `ConfirmBookingHandler`) actually produces. `Bookings.Jobs.ReconcileOrphanedBookedHoldsJob` is the real backstop for that state: it asks Availability (via `IHoldLookup.GetBookedHoldIdsOlderThanAsync`) for `'booked'` holds older than a grace period, checks which have no matching `bookings.hold_id` row, and releases the orphans through the existing `IHoldConfirmation.ReleaseHoldAsync`. Deliberately owned by Bookings, not Availability - see [ADR-0004](0004-module-boundaries-via-contracts-projects.md)'s Consequences for why that module placement matters and how the boundary stays compiler-enforced. (`IHoldLookup`/`IHoldConfirmation` originally lived in `Catalog.Contracts` - both moved to `Availability.Contracts` when `UnitAvailabilityHold` was extracted into its own module, with no change to either interface's shape.)
- **The reconciliation job's own window is a second, smaller accepted risk window, not a closed one.** It only looks at `'booked'` holds within a bounded lookback (a 2-day `ReconciliationWindow`, capped at 1000 candidates per run) to avoid scanning this app's entire booking history every 5 minutes. An orphan older than that window - or one that arrives in a run that hits the 1000 cap while older orphans are still queued behind it - is invisible to the job forever, not just delayed; it logs a warning when it hits the cap, but recovering a hold past the window itself is a manual query (find `'booked'` holds with no matching `bookings.hold_id` row, call `ReleaseHoldAsync` by hand), not something any job will do for you.
- Every new cross-module write path should be evaluated against this same checklist (authoritative-write-first ordering, idempotent compensation, exceptions preserved on double-failure) rather than inventing a new ad hoc approach each time.
- If the outbox alternative above is ever adopted, it should likely replace *all* of these hand-written compensations at once, not be introduced piecemeal for just the newest workflow.
