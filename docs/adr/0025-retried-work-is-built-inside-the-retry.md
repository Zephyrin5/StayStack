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

## Amendment: a completion marker in one module cannot gate work recorded in another

The refund redesign replaced a pair of guessing conditionals with a durable `RefundObligation` plus a single resolver. The obligation carries a `ResolvedAt` marker, and treating that marker as authority reintroduced the same class of failure one level up.

> **Bookings describes what cancellation _requires_. The committed Transactions refund record establishes whether that requirement has been _fulfilled_. `ResolvedAt` is recoverable bookkeeping so the sweep can skip rows cheaply - never an authority that can prevent a refund.**

The general rule, which is the one to carry forward:

> **A flag committed in module A must never gate work whose completion is recorded in module B. Read B's own state to decide whether the work happened; use A's flag only to skip cheaply when it agrees.**

That is verify-before-compensate applied to a completion marker instead of a retry, and it fails the same way when ignored.

### Why the marker cannot be trusted

`OutboxDispatcherBase.ClaimAndDispatchAsync` runs `TryHandleAsync` **inside** its claim transaction, and the resolver writes to two databases. Which of those two writes is inside a transaction therefore depends on which dispatcher called it, and the two are mirror images:

| Caller | Refund (Transactions) | Marker (Bookings) |
|---|---|---|
| `TransactionsOutboxDispatcher` | joins the dispatcher's transaction - **uncommitted** | own connection - **commits immediately** |
| `BookingsOutboxDispatcher` | own connection - **commits immediately** | joins the dispatcher's transaction - **uncommitted** |

So both of these are reachable:

- **Marker without refund.** The dispatcher transaction fails after the marker committed. The retry saw `IsResolved` and returned; the dispatcher marked the message processed; the sweep skipped the row on `ResolvedAt != null`. **The refund was lost permanently, with every mechanism reporting success.**
- **Refund without marker.** The process dies before the marker. The resolver queried for `Succeeded` alone, so every later run found the now-`RefundPending` transaction invisible and returned at the first step - leaving the obligation unresolved forever and re-processed by the sweep every cycle.

The fix is one idea applied twice: query for `Succeeded` **or** `RefundPending`, treat `RefundPending` as "the refund is durable, only the bookkeeping is outstanding", and never early-return on `IsResolved`.

### Consequences

- **The `already-finalized` catch had the same hole**, and its own comment claimed the obligation was "still marked below" - which the `return` made false. It now finishes the bookkeeping.
- **That catch also had to widen, and only an intermittent failure revealed it.** Two concurrent resolvers each load the transaction as `Succeeded`, so both pass `MarkRefundPending`'s in-memory guard and the loser is caught by the xmin concurrency token instead - `DbUpdateConcurrencyException`, not `TransactionAlreadyFinalizedException`. It now catches both and, rather than inferring from which arrived, re-reads to confirm a refund exists before marking anything.
- **A backstop sweep must not be ordered by the event that created its rows.** Both writers record an obligation whether or not a payment succeeded, and an unpaid one correctly resolves to nothing - so ordered by `CancelledAt` and capped per run, a backlog of unpaid rows held the front of the queue permanently and no newer obligation with money behind it was ever reached. That is the ordinary workload, not an error: most cancellations are of unpaid bookings. `NextAttemptAt` with backoff fixes it, and `Attempts` makes the cap warning legible - a capped batch of high-attempt rows means "waiting on payments that may never come", a capped batch of fresh ones means genuinely behind.
- **Do not resolve an unpaid obligation to clear it.** A late payment is exactly the case the row exists for; marking it settled to tidy the queue throws that case away.

## Amendment: payment-attempt identity, and who may clear a change tracker

Three of the four defects in this round were the same wrong assumption - *one payment attempt per booking* - and nothing in the schema says so.

> **A booking may have multiple payment transactions. Any path that knows which attempt it concerns resolves by transaction id. Any path that is genuinely booking-wide must tolerate several, ordered deterministically. `ix_transactions_booking_id_active` constrains `Pending` and `Succeeded` only and is not a uniqueness guarantee for anything else.**

`ConfirmBookingPaymentOutboxMessage` has carried `TransactionId` since it was written, and the confirmation path scanned by booking anyway - which is what manufactured the ambiguity it then could not read. A `RefundPending` attempt beside a `Succeeded` one is legal, and every `SingleOrDefaultAsync` over "this booking's transactions" threw on it, on every retry and every sweep pass, for as long as both rows existed.

Two supporting notes:

- **Order by `Id`, not `SucceededAt`.** Version-7 GUIDs are already creation-ordered, they are never null - `SucceededAt` is, on rows predating that column - and SQLite cannot `ORDER BY` a `DateTimeOffset` at all, which the unit tests run on.
- **The pair is reachable, which is why initiation had to be locked.** `InitiateTransactionHandler` checked the booking was payable and inserted as independent operations, so a cancellation committing between them created a payment against a cancelled booking - and if that cancellation had moved an earlier attempt to `RefundPending`, the active-transaction check matched nothing and the pair was born. What was filed as "untidy, no money moves" was manufacturing the state that breaks refund resolution permanently.

### The lock had to be advisory, again

Cancellation locks the booking row with `FOR UPDATE`, which is database-wide and would exclude a payment perfectly well - except that Transactions cannot take it without naming `bookings`, the coupling docs/adr/0004 exists to prevent. `BookingPaymentLock` is the same answer `UnitAvailabilityLock` gave to the same question: a key both sides agree on without either reaching into the other's schema. Taken exclusively by both, since two concurrent initiations are already refused by the active index and there is no parallelism worth preserving.

#### Every cancelling path takes it, advisory lock first

> **Every path that cancels a booking takes `BookingPaymentLock`, in the same transaction as the cancellation. A path taking both locks takes the advisory lock first, then the booking row lock.**

The lock went into cancellation and initiation, and not into `ExpireUnpaidBookingsJob` - the other path that cancels a booking. Expiry held the booking row alone, and initiation never touches that row, so the two could not see each other: initiation re-read `Pending` under its lock, expiry cancelled and released the unit, and initiation committed a payment against a booking that no longer existed. Exactly the state the lock was introduced to prevent, through the one participant nobody updated.

Once two paths take both locks, their order is load-bearing. Advisory first, because a path waiting on it then holds no row lock while it waits, so payment confirmation - which takes the row lock alone - is never queued behind an unrelated initiation. `CancelBookingHandler` took them the other way round and was reordered.

Expiry takes it with `pg_try_advisory_xact_lock` rather than waiting, the advisory counterpart of the `SKIP LOCKED` row claim it already made: a sweep steps over contended work and revisits it next run. Both are inside one transaction, so a skip at either releases the other with the rollback.

`BookingPaymentLockProtocolTests` pins both halves from source - every file calling `Booking.Cancel` must take the lock, before the row lock and before the cancel - because this failure is invisible from inside any file that changed.

What the lock does **not** exclude: an existing `Pending` payment *succeeding*. `MarkTransactionSucceededHandler` does not take it, so expiry's microseconds-wide tie between its succeeded-payment guard and its commit remains. The refund obligation compensates it.

### Only the owner of a transaction may clear its change tracker

> **A component that does not own the transaction does not clear the change tracker. Discard the specific entity you need to discard.**

`TransactionReversal`'s concurrency catch called `ChangeTracker.Clear()` on a scoped `AppTransactionsDbContext` - the same instance `TransactionsOutboxDispatcher` was using to track the `OutboxMessage` it was in the middle of processing. The message was detached, `ProcessedAt` was assigned to a detached entity, `SaveChangesAsync` wrote nothing, and the dispatcher reported success over a message still pending.

It self-healed on redelivery, which is worse rather than better: the reported outcome and the persisted state disagreed and nothing said so. `Entry(entity).ReloadAsync()` discards exactly what needs discarding.

This is the second time a shared-context `Clear()` has caused a problem here; the first was `OutboxDispatcherBase` clearing it out from under a handler.

### One observation, not two reads of moving state

`CancelBookingHandler` asked for the refund snapshot and then for the succeeded amount. A dispatcher or the sweep can move a payment `Succeeded -> RefundPending` between them, and then the first sees no refund and the second sees no payment - so the response reported no refund at all, describing neither the state before nor the state after.

Reordering cannot fix that; there has to be one read. `GetPaymentStateAsync` returns status, original amount and refund amount together, and both branches of cancellation derive from it.

The same drift had appeared inside the resolver: the repair step accepted `RefundPending` alone while the concurrency catch asked `!= Succeeded`. One counted too few - a refund reaching `Refunded` or `RefundFailed` before its marker was written became invisible and its obligation never settled - and the other too many, since `Failed` means no refund was written at all. Both now call one predicate.

### Entities never mint their own identity

> **Every entity factory takes a caller-supplied `Guid id`. The creating handler mints it on the first line of `Handle`, before anything that could retry.**

Five factories minted, and every caller happened to call them outside its retry delegate - correct by line order alone. `EntityIdentityProtocolTests` now checks the convention by direct match, with ASP.NET Identity's `ApplicationUser` and `RefreshToken`'s initializer named as exemptions. `Entity.SetCreated` records why `Id` is not assigned there: the interceptor runs inside every retried delegate.

Converting the factories did **not** make `RetryIdentityProtocolTests`' call resolution redundant. Against the pre-fix sources, a delegate-body-only scan missed `InitiateTransactionHandler` (through a helper), `RefreshTokenHandler` and `SignUpHandler` (through a service method), and caught only `PromotionRedemption`.

**"No retry delegate" is not "no retry".** `EnableRetryOnFailure` wraps a plain `SaveChangesAsync` in the execution strategy too, and EF sends a single-row insert with no transaction at all. Probed: a lost acknowledgement on that autocommitted insert retries into its own committed row and surfaces `23505`. The seven create handlers built on a bare save - `CreateProperty`, `AdminCreateProperty`, both promotion creates, both review creates and `CreateHost` - answered a 500, or for promotions and reviews a confident wrong domain error ("code already in use", "already reviewed"), for a row that existed.

They now recover through `Persistence.CommittedInsertRecovery`: on a unique violation **of the entity's own primary key**, clear the tracker, read the committed row back, and return the response the first attempt would have (every one of these returns only the id). Any other unique index keeps its domain answer - a test pins that a real promo-code conflict still returns 400, and fails with a 500 if the match is widened to any unique violation.

Matching by constraint name rests on two database facts, both pinned by `PrimaryKeyConstraintTests`: the PK carries the model's name (taken from the EF model, not a literal), and it is the table's oldest unique index. The second is not a formality. A re-inserted row violates the PK and every other unique index at once, and Postgres reports the first it checks, in index OID order - probed, a unique index created before the PK is reported instead of it. A migration that ever recreates one of these primary keys would silently turn recovery back into the wrong domain error.

`CommitFaults.FailAfterAutocommit` simulates this case; `FailAfterCommit` never fires for a statement EF sent without a transaction.

