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

## Amendment: the rule needed a second half, and it needed applying twice more

Three further sites, found in two rounds after this was written. The rule was right; it was under-applied and under-stated.

### Two more handlers built retried work outside the retry

`DeleteUnitHandler` loaded its `Unit` before `strategy.ExecuteAsync` and called `Archive()` on it inside - while `DeletePropertyHandler`, written in the same change, cleared and reloaded. The retry then assigned `Archived` to an entity whose *original* value was already `Archived`, EF saw no change, the `UPDATE` carried the audit columns alone, and a 200 was returned over a still-active unit.

`UpdatePricingRuleHandler` had the same shape and is worse, for two reasons. It runs at `Serializable` and retries `40001` as its normal operating mode rather than as an exotic failure - so the path is *ordinary* there. And it carried a comment arguing the arrangement was deliberate:

> No `ChangeTracker.Clear()` here, unlike Create, and deliberately not - `rule` was loaded once, before the retry strategy starts, and stays the SAME tracked instance across every retry; clearing it would detach it, breaking `SaveChangesAsync`'s ability to see the mutations on a retried attempt.

That is backwards. The mutations are applied *inside* the delegate, so clearing and reloading yields a fresh instance that then receives them. **Keeping the instance is what loses them.** A price update returned 200 and left the old price in the database.

The sweep cleared the other fourteen `strategy.ExecuteAsync` sites, and the reason most are safe is worth recording, because it is not obvious: **`Add` is not exposed to this defect.** After `Clear()` an `Added` entity is re-`Add`ed and re-inserted; only a *loaded* one is silently seen as unchanged. So `CreateUnitHandler` and `SignUpHandler` are fine. `PromotionRedemption` and `HoldAvailabilityHandler` are raw SQL, `RefreshTokenHandler` makes no tracked writes, and the rest already reload inside.

### The rule extends past retries: nothing observable may depend on an uncommitted write

`ConfirmBookingHandler.RecoverOwnCommittedAttemptAsync` called `ReplayAsync` - which mints a management token and saves its hash - while the caller's transaction was still open. The caller's `await using` then rolled it back. The guest received **200, a real booking id, and a credential that fails at first use.**

The sibling branch in `BeginConfirmationAsync`'s unique-violation catch did the same work correctly, because it happened to roll back first. One branch remembered and one did not, which is the whole diagnosis: the boundary lived in each branch's discipline rather than in the shape of the code.

**Stated as a rule: a transaction's outcome is decided before anything observable leaves the handler. Nothing returned to a caller may depend on a write in a transaction that has not committed.**

`ConfirmationStart` now carries the `CheckoutIdempotencyRecord` - the *decision* - instead of the response built from it, and `Handle` performs the replay once the transaction is resolved and the execution strategy is behind it. A fourth branch added here cannot reproduce the bug, because no branch is in a position to issue a credential.

### Two checks guarding one invariant must be ordered against the state machine

Not a retry rule, and it earns its own line. `UnitArchival.EnsureArchivableAsync` asks two questions - is there a live booking, is there a live hold - and `HasActiveHoldForUnitAsync` deliberately excludes `'booked'`. The advisory lock excludes *new* holds but does not freeze an existing one, so a hold can run `held -> pending_payment -> booked` between the two checks. With bookings checked first, a checkout completing in that gap was invisible to both, and the unit was archived with a live paid stay against it.

**Where two checks guard the same invariant against a state machine that can advance between them, order them so the later check covers the states the earlier one can transition into.** Here that means holds first: `'booked'` implies a `Confirmed` booking, since `MarkHoldPaidAsync` sets it in the same transaction as `Booking.Confirm()`.

The test gates on whichever check runs first rather than on a named one, so it pins the property rather than the current order - a test hard-coded to the order would go green the moment someone reordered the method, which is the exact edit the comment in that method exists to prevent.

## Amendment: identity is generated outside the retry, and constraints are matched by name

The third and fourth call sites of the same principle, found the round after the first amendment.

`EnableRetryOnFailure` cannot distinguish a failed transaction from one that committed and lost its acknowledgement. It re-runs the delegate either way - so a delegate that mints a fresh id on each attempt collides with its own predecessor, and then reports that collision as somebody else's conflict.

`HoldAvailabilityHandler` generated its `holdId` inside the retried delegate. A retry inserted a *different* id over the same range, the GiST exclusion constraint refused it, and the caller got 409 for a hold that exists. Worse than losing the response: the orphan carries the same `client_key`, and the per-client cap counts `held` and `pending_payment` by that key - so the guest lost the range *and* burned one of their concurrent slots for the hold's full lifetime, repeatedly on a flaky connection.

`InitiateTransactionHandler` has no explicit delegate, and its id was already stable - `Transaction.Create` runs once, outside the `SaveChangesAsync` that EF retries. Its defect was the catch:

```csharp
catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
    throw new TransactionAlreadyInProgressException(request.BookingId);
```

Two unique indexes reach that, meaning opposite things. `ix_transactions_booking_id_active` is a real conflict. The primary key is *our own committed insert*, and answering "already in progress" is true and useless - the guest cannot learn the id of the transaction they just created, so the payment is stranded behind a number nobody can see.

**The rule: any write that creates a row under a retrying execution strategy generates its identity outside the retried delegate, and treats a collision on that identity as "my earlier attempt committed" - read the row back and return it. Constraint matching is by name, never by `SqlState` alone.**

Two details the implementation turned up:

- **Do not branch on the SqlState even when the codes look distinct.** A re-inserted hold violates the primary key (`23505`) *and* overlaps itself on the exclusion constraint (`23P01`), and Postgres does not promise which it reports. The pre-generated id answers directly - does a row under *my* id exist - so both codes route to the same question rather than to different answers.
- **An unrecognised constraint must not be assumed to be ours.** `InitiateTransactionHandler` rethrows anything that is neither the active-transaction index nor the primary key, because guessing it is our own committed insert would report success for a write that never happened.

### Simulating a lost acknowledgement takes the right hook

Three were tried, and only one is correct for a given handler:

- `TransactionCommittingAsync` fires *before* the commit, so it proves the rollback case and nothing else. The existing archival tests used it and were only ever testing half the failure.
- `TransactionCommittedAsync` is the lost acknowledgement for a handler that opens an explicit transaction. It never fires for one that does not - `InitiateTransactionHandler` leaves EF's implicit transaction to it - and that test silently passed through without triggering.
- `SavedChangesAsync` does fire there, but runs **outside** the region the execution strategy retries, so throwing produced a 500 rather than a second attempt. The simulation has to sit where a real lost acknowledgement sits: inside the retried work. `ReaderExecutedAsync`/`NonQueryExecutedAsync`, immediately after the INSERT has executed, is that place.
