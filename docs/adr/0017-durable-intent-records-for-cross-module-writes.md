# 0017 - Durable intent records for the forward half of cross-module writes

**Status:** Accepted (partially supersedes [ADR-0003](0003-compensating-actions-over-distributed-transactions.md))

## Context

ADR-0003 split every cross-module write in two: a *forward* call whose result the current write needs synchronously stays a direct call with hand-written compensation, and a *follow-up* that runs after a local write is already durable goes through the transactional outbox, enqueued in the same `SaveChangesAsync()` as the state change it accompanies.

Two of the four workflows genuinely satisfy that guarantee. In `CancelBookingHandler`, `booking.Cancel()` and all three `dispatcher.Enqueue(...)` calls flush in one `SaveChangesAsync`; in `MarkTransactionSucceededHandler`, so do `transaction.MarkSucceeded()` and its enqueue. There is no window in which one exists without the other. **Neither is changed by this ADR.**

`ConfirmBookingHandler` did not. `IHoldConfirmation.ConfirmHoldAsync` commits to Availability's own database before anything exists in Bookings, so a process death on the next line left *nothing* to recover from - not a pending outbox row, not a dead-lettered one. `IPromotionRedemption.RedeemAsync` had the identical exposure. ADR-0003 acknowledged this and covered it with two reconciliation jobs, but the shape of those jobs was the problem:

```csharp
IReadOnlyList<Guid> staleBookedHoldIds = await holdLookup.GetBookedHoldIdsOlderThanAsync(...);
List<Guid> holdIdsWithLiveBookings = await dbContext.Bookings
    .Where(b => staleBookedHoldIds.Contains(b.HoldId) && b.BookingStatus != BookingStatus.Cancelled)
```

Each asked another module for candidates over a rolling 2-day/1000-row window, then joined the answer against Bookings in application memory. That is a heuristic reconstruction of a fact nobody recorded, and it carried real costs: an orphan older than the window was unreachable *forever*, not merely delayed; two cross-module lookup contracts (`IHoldLookup`, `IRedemptionLookup`) existed solely to feed it; and the join itself was the coupling ADR-0004's boundaries exist to avoid - a hold's `booked` status being meaningful only relative to a Bookings row does not become easier to extract for having avoided a shared transaction. It just moved the join into C#.

## Decision

**Write a durable intent record first, in the consuming module's own database, before the first cross-module call.** `ConfirmBookingHandler` inserts a `PendingBookingIntent` (keyed by the pre-generated `bookingId`, unique on `HoldId`) and commits it before calling `ConfirmHoldAsync`. Every subsequent exit - success, ordinary exception, hard process death - either deletes that row or leaves it as an explicit, self-describing work item.

`ReconcileOrphanedBookingIntentsJob` then queries **only Bookings' own table**, and compensates both sides off the one row: `ReleaseHoldAsync(intent.HoldId)` and `ReverseRedemptionAsync(intent.Id)`. The intent's `Id` *is* the `bookingId` already passed to `RedeemAsync`, and `ReverseRedemptionAsync` no-ops when nothing was redeemed, so one job covers both without knowing whether a promotion was ever involved. `IHoldLookup` and `IRedemptionLookup` disappear.

### The intent lives in the consuming module, not the producing one

The obvious alternative - have `ConfirmHoldAsync` record "a booking is expected for this hold" in the *same transaction* that flips the hold to `booked` - gives the same durability with genuine atomicity, and was rejected anyway. It puts a Bookings-shaped fact in Availability's schema: that module would carry a column meaningful only to a consumer it must not know about. That is the same semantic coupling as the in-memory join, relocated from the query into the schema, and it survives extraction *worse* - a separated Availability service would own a field it cannot interpret.

Writing the intent first, in Bookings, in Bookings' own transaction, achieves the same "every failure mode leaves a marker" property with no cross-module artifact at all. Bookings already owns the concept of a booking being expected; that is what the module is about.

### The guarantee is structural, not temporal

Reconciliation and a live request can both act on the same hold, and **timing cannot separate them**. Nothing in `ConfirmBookingHandler` re-validates the hold after `ConfirmHoldAsync` - the booking insert is unconditional. A job firing mid-request would release a genuinely-`booked` hold and reverse a genuinely-live redemption, after which the request completes and inserts a confirmed `Booking` whose hold is back to `held` with `hold_expires_at = @Now` (immediately re-bookable by anyone) and whose discount is still in `TotalPrice` with `redemption_count` decremented. Both silent.

`FOR UPDATE SKIP LOCKED` does not prevent this - it only covers the ordering where the request locks first. Nor does any grace period: `EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: 30s)` sits beneath `ConfirmHoldAsync`, `RedeemAsync`'s own transaction, `GetUnitAsync`, and the final save, so a contended-but-healthy request can exceed minutes without anything being wrong.

So the mechanism is this: **on the success path the intent delete rides in the same `SaveChangesAsync` as the `Booking` insert, and EF's affected-row assertion on a zero-row delete is what makes the booking impossible to write once the job has reconciled.** All four interleavings resolve correctly:

| Ordering | Outcome |
|---|---|
| Job claims and commits before the request's save starts | Request's delete affects zero rows → throws → no `Booking` written |
| Job wins the row lock; request's delete blocks behind it | Same - the delete finds nothing once the lock releases |
| Request's delete takes the lock first | Job's `SKIP LOCKED` claim skips the row entirely |
| Request already committed | Job finds no row to claim |

A 10-minute grace period and `*/5` cadence remain, but they now only trade how long a crashed confirm holds inventory against how often the job needlessly races a slow request (costing that guest a 409 and a re-hold). Shortening it should follow from introducing an *enforced* request timeout, so the bound is real rather than assumed.

### Tracked delete on the success path; `ExecuteDelete` on every failure path

EF asserts affected rows for *every* tracked delete, not only ones carrying a concurrency token. That is precisely what makes the success path work, and precisely what makes it wrong everywhere else: a zero-row delete batched with the compensating `Enqueue` calls would throw, roll those outbox rows back so they are **never written**, and replace the meaningful exception (`PromotionInvalidException`, `ValidationException`) with an EF concurrency error. Every failure path therefore uses `ExecuteDeleteAsync` - a direct statement with no row-count assertion, so zero rows is a clean no-op - issued *after* the compensating save, and detaching the instance afterward since `ExecuteDelete` bypasses the change tracker.

### Verify before compensating; never infer from the exception type

`SaveChangesAsync` runs under a retrying execution strategy, and such a strategy **cannot distinguish a failed transaction from one that committed and lost its acknowledgement**. It re-runs the batch, which then fails against its own already-committed rows. Which exception surfaces depends on EF's internal command ordering - a duplicate-key `DbUpdateException` if the insert replays first, a zero-row `DbUpdateConcurrencyException` if the delete does - and that ordering is not contractual.

So the catch block asks the database, once, before branching on anything: is there a `Booking` with this pre-generated id? If yes, an earlier attempt committed and the correct response is success - compensating there would release a live booking's hold and reverse its redemption, then report a 500 for a booking that actually succeeded. Only when no booking exists does `DbUpdateConcurrencyException` get its honest meaning ("the job reconciled this"), and only then does compensation run.

The same hazard sits on the intent insert itself, and is resolved by the same principle: a unique violation triggers one read of the existing row, and if its `Id` matches this request's `bookingId` it is *our own* committed insert, so the request carries on rather than reporting a conflict about itself.

**Generalized: every save under a retrying execution strategy needs this.** Pre-generate the identity, and on failure ask the database what actually happened. "Compensations are idempotent, so running them twice is safe" holds only when the forward work genuinely didn't happen.

One EF subtlety this depends on: when the request carries on after its own committed insert, the tracked instance is still `Added`, and `Remove` on an `Added` entity transitions it to `Detached` rather than `Deleted` - emitting no `DELETE` at all. That would silently disable the success-path assertion *and* leave the row alive behind a confirmed booking. The handler therefore detaches the stale instance and attaches the fetched row as `Unchanged` before continuing.

### Refuse a concurrent confirmation rather than adopting its intent

A second request for a hold whose intent is already live gets a `ConflictException` (409), not a takeover. Adopting the existing intent would replay a redemption that may already hold the `(promotion_id, guest_email)` slot. The two conflict messages are distinguished by the existing row's `CreatedAt`: inside the grace window, a genuinely concurrent confirmation is in progress; outside it, a crashed one is awaiting cleanup - which is accurate rather than claiming something is "in progress" when nothing is.

Note this differs from the shape the Identity/`BecomeHost` version takes (now implemented - see the amendment below), where adopting the pending row is correct because `RegisterHostAsync` is idempotent by caller-supplied id. Two shapes, one pattern, because the underlying resources differ: a hold is consumed exactly once by deliberate business rule (the exclusion constraint in ADR-0010 exists for that), while a `Host` row is not.

### The reconcile job's claim must share a fate with its work

`ReconcileOrphanedBookingIntentsJob` claims each row with `FOR UPDATE SKIP LOCKED` *inside* a transaction, performs both compensations, deletes the row, and commits - mirroring `OutboxDispatcherBase.ClaimAndDispatchAsync`. An autocommitting claim (`UPDATE ... RETURNING`) would resolve the intent before the work it authorises, so a death in between would strand the hold forever with no marker: the exact bug class this ADR removes, one layer down.

**Stated precisely: the claim and the delete commit together; the cross-module work is idempotent and may repeat.** It is *not* all three atomically - `ReleaseHoldAsync` runs on `AppAvailabilityDbContext` and `ReverseRedemptionAsync` on `AppPromotionsDbContext`, separate connections committing independently. A rollback anywhere means the next run repeats both calls, which their contracts allow. Like the outbox dispatcher, this holds a row lock across cross-module round trips - deliberate, acceptable because it is one row at a time under a per-run cap, and `SKIP LOCKED` means a concurrent run steps over rather than blocking.

### The Cancelled-booking case leaves the reconcile job

The old job also covered a hold left `booked` behind a `Cancelled` booking. That coverage is now redundant: `CancelBookingHandler` writes `ReleaseHoldOutboxMessage` in the same `SaveChangesAsync` as `booking.Cancel()`, so the row is guaranteed durable, and `SweepDeadLetteredAsync` retries a dead-lettered row hourly with no terminal give-up. Generic outbox machinery covers it forever.

Because this ADR makes that forever-retry load-bearing, its observability had to become real. Previously a sweep retry that failed again emitted nothing at all - `becameDeadLettered` is false by construction on a re-crossing, since `Attempts` is already past `MaxAttempts` - so a permanently-broken message looped hourly, invisibly. `OutboxTelemetry.DeadLetterRetried` plus a `Warning` now mark each such retry, with the existing `Error` + `DeadLettered` still reserved for the first crossing.

## Amendment: BecomeHost, the last one

`BecomeHostHandler` was the only remaining forward-half cross-module write in
the codebase not covered by an intent row or an outbox row in the same
transaction as its state change. It now is, using the shape this ADR predicted
for it rather than the Bookings shape.

Two distinct defects, both closed:

**The unrecoverable window.** `RegisterHostAsync` commits a `Host` in Hosts'
database before Identity writes anything. The `!updateResult.Succeeded` and
failed-role branches compensate correctly through the outbox, but a hard
process death between the two wrote nothing anywhere - no intent, no outbox
row, and no reconcile job existed in Identity. The orphaned `Host` was
permanent, with no row anywhere pointing at it.

**The compounding retry.** `RegisterHostAsync` generated the id and returned
it, so a client retrying after a timeout was indistinguishable from a first
attempt: the `user.HostId is not null` guard still saw null, and each retry
created another orphan. Three retries on a flaky connection left three.

The fix, as written down here originally: a caller-supplied `hostId`,
`RegisterHostAsync` as an upsert by that id, a `PendingHostLinkIntent` in
Identity's own database keyed on it, and `ReconcileOrphanedHostLinkIntentsJob`
calling `DeleteAsync`. Smaller than the Bookings version - there is no
promotion leg, so one compensating call rather than two.

Two details worth stating, because they are what make it correct rather than
merely present:

- **The intent delete is batched into `UserManager.UpdateAsync`'s own
  `SaveChanges`.** `UserManager` resolves the same scoped
  `AppIdentityDbContext`, so marking the intent `Remove`d *before* calling it
  makes the delete and the `HostId` write commit atomically. That is what
  makes it impossible for the job to delete a live `Host`: a linked user has
  no surviving intent, and a surviving intent means the link never committed.
  Resolving the intent separately, after the update, would open exactly the
  window the job exists to close. This is the direct analogue of the tracked
  delete in `ConfirmBookingHandler`.
- **A retry reuses the existing intent's id** rather than allocating a new
  one, which is what actually bounds the orphan count at one; the unique index
  on `UserId` is the backstop for two attempts racing past that lookup, failing
  the second insert before any cross-module call happens.

Known remaining gap, recorded rather than fixed: a death after the `HostId`
link commits but before `AddToRoleAsync` leaves a user linked to a real `Host`
without the `Host` role. That is not an orphan - nothing is unreferenced - and
it is not what this pattern addresses, but it does leave the user unable to
retry, since the `AlreadyAHostException` guard now trips. Fixing it belongs
with role assignment, not with intent records.

## Consequences

- **`IHoldLookup` and `IRedemptionLookup` are deleted**, along with both old reconcile jobs - the concrete extraction-readiness win, not a relocation of the coupling.
- **The old jobs were deleted in the same release, which is safe only because this application has no deployed environment yet.** The general rule is the opposite: replacing a running recovery mechanism needs an overlap release, because an orphan created *before* the intent table existed has no intent row, so the new job cannot see it while the old one is already gone - stranding those holds `booked` permanently. That rule does not bind here. There is no `appsettings.Production.json` (the base `AppConnection` is empty and supplied per environment), no container/IaC/deploy step in CI, and no release tags: no deployed database exists to hold pre-existing orphans. **Anyone re-running this kind of replacement against a live database must reinstate the overlap** - ship the new mechanism first, leave the old one running for a release, then delete. A backfill migration is not the alternative: synthesizing intent rows for existing orphans needs a raw `unit_availability_holds` ← `bookings` join inside a Bookings migration, which is the boundary violation ADR-0004 forbids and only works while the modules share a physical database.
- **`booked_at`'s index is dropped and the column is now write-only.** `HoldLookup.GetBookedHoldIdsOlderThanAsync` was its only reader, so `ix_unit_availability_holds_status_booked_at` served no query while still costing every hold write; `DropUnusedBookedAtIndex` removes it. The column itself is kept - "when was this hold consumed" is genuine diagnostic state for one timestamp - but nothing queries it, so no index or reader should be assumed.
- **A new, narrow failure mode:** a crash between the intent insert and `ConfirmHoldAsync` leaves a perfectly usable `held` hold that the intent blocks from confirmation until the job runs (~10-15 minutes). No data loss, and it did not exist before this ADR.
- **A reconciled hold is immediately expired**, since `ReleaseHoldAsync` sets `hold_expires_at = @Now`. A guest retrying the same `HoldId` gets the existing 404 and must re-hold - which is why the "timed out and was rolled back" 409 says *start over* rather than *try again*.
- **`ConfirmBookingEndpoint` now returns 409** for both conflict shapes.
- **This does not remove the mutable `redemption_count` or the reversal contract.** Deferring the redemption until after the `Booking` exists was considered and rejected: `ConfirmBookingHandler` bakes the discount into `Booking.TotalPrice` before the booking is written, so the cap check is a genuine synchronous forward dependency, not a follow-up. Splitting it into a reservation finalized by an outbox message was also rejected - that reintroduces a real window (up to ~6.5 hours pre-dead-letter) in which a live booking's discount is applied while Promotions still reads `Reserved`, and a timer-driven cleanup job cannot tell "orphan" from "in flight". The Bookings intent answers that question exactly, with no window.
- **The orphan-cleanup story depends on `ix_promotion_redemptions_promotion_email` being partial on `reversed_at IS NULL`.** A reversal genuinely frees the `(promotion_id, guest_email)` slot, so a guest whose confirmation was reconciled can redeem the same code again on their retry.
- Every new cross-module write should be evaluated against this checklist alongside ADR-0003's: does the *first* cross-module call commit somewhere this module cannot see? If so it needs an intent record, not just compensation - because compensation can only run if the process survives to run it.
