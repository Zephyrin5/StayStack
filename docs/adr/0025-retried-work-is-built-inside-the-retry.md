# 0025 - Work that must survive a retry is built inside the retry

**Status:** Accepted

Extends [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md)'s verify-before-compensate rule from `SaveChanges` ambiguity to execution-strategy retries, and applies [ADR-0003](0003-compensating-actions-over-distributed-transactions.md)'s outbox rule to the two reconcilers that were exempting themselves from it.

## Context

Three call sites made the same mistake in different places: **work that must be retried was constructed outside the thing that retries it, or committed before the decision that authorised it.**

They were not sloppy. Each had a comment defending its arrangement, and each comment was wrong in a way that reads as careful.

### `CancelBookingHandler` lost the cancellation on retry

The mutation and both `Enqueue` calls sat outside `strategy.ExecuteAsync`. `SaveChangesAsync` defaults to `acceptAllChangesOnSuccess: true`, so **by the time it returns, the mutated `Booking` and both `OutboxMessage`s are `Unchanged`** - accepted, though the transaction has not committed.

A transient failure on `CommitAsync` then retried a delegate with nothing left to save. `ReleaseHoldAsync` ran again, because it is a statement rather than tracked state. The save wrote nothing. The commit succeeded. The handler returned `200 Cancelled` over a database holding a `Confirmed` booking, a released hold, and zero compensating rows - so the relay backstop had nothing to deliver, because the rows were never written.

The defending comment claimed the rows would be "still Added from the failed attempt". They are not.

### The reconcilers committed compensation before authorising it

`ReconcileOrphanedBookingIntentsJob` called `ReverseRedemptionAsync`, which writes `AppPromotionsDbContext` on its own connection and therefore **autocommits**, from inside a Bookings transaction that might still fail. A slow confirmation that had already redeemed got reconciled: the redemption was reversed, the transaction failed, the rollback restored the intent and the hold, and the original request resumed and wrote a booking whose discount had already been clawed back.

`ReconcileOrphanedHostLinkIntentsJob` had the identical shape with `IHostRegistrar.DeleteAsync`, leaving an account linked to a deleted host.

**Idempotency does not cover this, and that is the part worth stating.** A second reconciler run is a harmless no-op against an already-reversed redemption. That protection only holds while nothing else can succeed in between - and the original request can.

## Decision

**Work that must survive a retry is constructed inside the retried delegate. Cross-module compensation is committed as an outbox row alongside the local decision that authorises it, never before it.**

Corollaries, each earned by one of the failures above:

- **Clear the change tracker at the top of a retried delegate.** Otherwise a second attempt inherits the first's accepted entities and reproduces the bug in a new shape.
- **Reload and re-lock state inside the delegate** rather than closing over an entity read before the transaction opened. That instance is stale by the time the delegate runs and definitely stale on a retry, where it may describe a world the previous attempt already changed. Build the response from the reloaded entity too - reporting an outcome off an entity nothing verified is how the old failure managed to look like success.
- **On an ambiguous commit, verify persisted state by pre-generated identity** rather than inferring from the exception. For cancellation the equivalent is the status itself: a booking already `Cancelled` means an earlier attempt committed, which is success, not a conflict.
- **Lock the booking before the hold.** `BookingPaymentConfirmation` locks the booking then marks the hold paid; cancellation did the reverse, so a concurrent cancel and payment could deadlock. `40P01` is retriable and it self-healed - by retrying a whole transaction under contention, which is a cost with no benefit.

## Consequences

- **`SELECT id … FOR UPDATE` then load through EF, not one `FromSqlRaw`.** The obvious consolidation fails: `Booking` carries `Money` as a complex property, so EF composes a projection asking for `TotalPrice_Amount`, a column the snake_case convention never produced. The rewrite failed with `42703` until it used the pattern `ExpireUnpaidBookingsJob` and `BookingPaymentConfirmation` already use - both of which already document why.
- **A bare `IInterceptor` registration is never consulted in this codebase.** Each module hands EF one interceptor *by name* in its `AddDbContext` options, so EF's DI discovery is not in play. The first attempt at the regression test registered `IInterceptor`, saw zero commits, and passed - a green test proving nothing. It substitutes `AuditableEntitySaveChangesInterceptor`, which EF already resolves from DI, and asserts the injection fired.
- **Failure injection must be targeted.** The test host runs TickerQ, whose relay and sweep jobs commit on their own schedule, so "fail the first commit you see" is stolen by a background job. The interceptor is armed for one booking id.
- **Assert against a fresh `DbContext`.** A handler that accepted its changes and then failed to commit leaves an in-memory entity that reads exactly like success; a test inspecting the same context passes while the database is wrong.
- **Test the property, not idempotency.** Running a reconciler twice only proves the second run is a no-op, which was never at risk. The test lets the external compensation succeed, fails the local transaction, and asserts the original request's state is intact.
- **And check the assertion detects the defect.** The reconciler test was vacuous on first writing: `ReverseRedemptionAsync` is an `UPDATE` setting `reversed_at`, never a `DELETE`, so asserting the redemption row still exists passes just as happily against a fully reversed one. Confirmed by reversing it up front and watching the test stay green.
- **One existing test changed meaning rather than breaking.** A throwing `IHostRegistrar` used to prove a per-item guard stopped one bad row starving the reconcile queue. The registrar is not called in the loop any more, so that starvation is structurally impossible rather than caught - and the test now asserts the stronger property.
