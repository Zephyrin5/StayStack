# 0017 - Durable intent records for the forward half of cross-module writes

**Status:** Accepted

## Context

[ADR-0003](0003-compensating-actions-over-distributed-transactions.md) splits a cross-module write in two: a *follow-up* that runs after a local write is durable goes through the transactional outbox, enqueued in the same `SaveChangesAsync` as the state change; a *forward* call whose result the current write needs synchronously stays a direct call.

A forward call commits in another module before anything exists locally to point at it. A process death on the next line leaves nothing to recover from - no outbox row, no local record - and reconstructing the fact later means asking the other module for candidates and joining them against local state, which is a heuristic over an unbounded window and couples the modules through the join.

Two workflows have a forward call: `ConfirmBookingHandler` redeems a promotion (Promotions) before the booking exists, and `BecomeHostHandler` registers a host (Hosts) before the user is linked to it.

## Decision

**Write a durable intent record in the consuming module's own database before the first forward call, and resolve it in the same transaction as the work that completes the operation.** Every exit - success, handled failure, process death - either deletes the intent or leaves it as an explicit work item for a reconcile job that reads only the consuming module's own table.

### The intent lives in the consuming module

Recording "a booking is expected" in the producing module's schema would give genuine atomicity with the forward call, but puts a Bookings-shaped fact in a module that must not know Bookings exists. Bookings already owns the concept of an expected booking.

### Checkout

`BeginConfirmationAsync` runs one transaction: the conditional hold transition (`ConfirmHoldAsync`, `held → pending_payment`), the `PendingBookingIntent` insert (id = the pre-generated booking id, unique on `hold_id`), and the checkout idempotency record when a key was sent ([ADR-0022](0022-checkout-idempotency-keys.md)). Holds are in Bookings ([ADR-0021](0021-availability-is-part-of-bookings.md)), so the intent is present if and only if the hold moved. `RedeemAsync` then commits in Promotions, and the final save writes the `Booking`, the management-token hash, the record's `completed_at`, and a **tracked delete of the intent**.

**Concurrent confirmations for one hold are decided by the conditional `UPDATE`**, an exactly-one-winner compare-and-set on the hold row. A request that finds no `held` row looks for its own committed attempt by booking id (a retry of itself - it continues), then for a completed record under its idempotency key (it replays), and otherwise answers 404. A collision on `hold_id` means a failing confirmation has released the hold but not yet discarded its intent; it is transient, and answered with a 409 to retry shortly.

**Failure paths compensate through the outbox**: enqueue `ReleaseHoldOutboxMessage` (and `ReverseRedemptionOutboxMessage` when a redemption may exist), save, dispatch, then `ExecuteDeleteAsync` the intent and any incomplete record. The failure paths use `ExecuteDelete` because it asserts no row count; a tracked delete that matched zero rows would throw inside the compensating save and roll back the outbox rows it carries.

**The success path's tracked delete is what excludes the reconcile job.** EF asserts one affected row for a tracked delete, so once the job has removed the intent, the booking cannot be written:

| Ordering | Outcome |
|---|---|
| Job claims and commits before the request's save | the delete affects zero rows, throws, no `Booking` written |
| Job holds the row lock; the request's delete blocks | same, once the lock releases |
| The request's delete takes the lock first | the job's `SKIP LOCKED` claim skips the row |
| The request already committed | the job finds no row |

Timing cannot separate the two instead: the execution strategy (up to 6 retries, 30s max delay) sits under every step, so a healthy request can outlast any grace period.

### Verify before compensating

A save under the execution strategy can fail against its own committed rows ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)), and which exception surfaces depends on EF's command ordering. So the final save's catch reads the `Booking` by its pre-generated id before branching: if it exists, the operation succeeded and the handler returns it.

**On `DbUpdateConcurrencyException` with no booking, the reconcile job removed the intent, and the handler compensates both the hold and the redemption.** The job's own redemption reversal is not sufficient: it can be dispatched before `RedeemAsync` commits, find nothing, and be marked processed - leaving a consumed single-use code with no booking. Releasing the hold again is safe because of properties of that row, not of the operation:

- `ReleaseHoldAsync` matches `status IN ('pending_payment', 'booked')`; the job already set the row to `held`, so the second release matches nothing.
- The job set `hold_expires_at = now`, and `ConfirmHoldAsync` requires `hold_expires_at > now`, so the row can never be claimed again.
- Hold ids are never reused; a stranger's hold on the same range is a different row.

Widening `ReleaseHoldAsync` to include `held`, or reusing hold ids, would make this call release someone else's inventory.

### Host registration

`BecomeHostHandler` opens a `PendingHostLinkIntent` (unique on `user_id`) whose id is the host id. A retry reuses an existing intent, and a concurrent attempt that collides on the user index - or its own retry colliding on the intent's primary key - adopts the committed intent. Both attempts want the same outcome for the same user, and `RegisterHostAsync` is idempotent by caller-supplied id, so adopting produces one host; this differs from checkout, where a hold is consumed once and a second claimant is a conflict.

The intent must outlive every step whose failure it recovers: register, link `HostId`, add the `Host` role. Its delete is staged before `AddToRoleAsync`, which saves through the same scoped context, so the intent disappears only when the role is written. A role failure unlinks the user (checking the result), enqueues `DeleteHostOutboxMessage`, and discards the intent; if unlinking fails, neither the deletion nor the discard happens, because a host the user still points at must not be deleted.

### Reconcile jobs claim and resolve in one transaction

`ReconcileOrphanedBookingIntentsJob` and `ReconcileOrphanedHostLinkIntentsJob` pick up intents older than their `ReconcileGrace` (10 minutes), claim each with `FOR UPDATE SKIP LOCKED` inside a transaction, perform local compensation, enqueue cross-module compensation as outbox rows, delete the intent, commit, and dispatch after the commit. The booking job releases the hold (same database), enqueues the redemption reversal, and deletes any incomplete idempotency record. The host job clears `HostId` when it still equals the intent's id and enqueues the host deletion. An autocommitting claim would resolve the intent before its work and strand the work on a crash.

## Alternatives considered

- **Reconstruct orphans by querying the producing module and joining locally.** Rejected: an orphan older than the query window is unreachable forever, and the join is the coupling module boundaries exist to prevent.
- **Record the expectation in the producing module.** Rejected: a consumer-shaped fact in a module that must not know the consumer.
- **Separate request and job by a grace period.** Rejected: nothing bounds a healthy request's duration below the retry policy's.
- **Defer the redemption until after the booking exists.** Rejected: the discount is part of `TotalPrice` before the booking is written, so the cap check is a synchronous dependency. Splitting it into a reservation finalised by an outbox message leaves a window in which a live booking's discount is applied while Promotions reads it as reserved, and a timer cannot tell an orphan from an in-flight request.
- **Adopt a concurrent checkout's intent.** Rejected: it would replay a redemption that may already hold the `(promotion_id, guest_email)` slot.

## Consequences

- A reconciled hold is immediately expired (`hold_expires_at = now`); a guest retrying the same `HoldId` gets 404 and must re-hold, which is why the "rolled back" 409 says to start over.
- Orphan cleanup depends on `ix_promotion_redemptions_promotion_email` being partial on `reversed_at IS NULL`, so a reversal frees the slot for the guest's retry.
- `booked_at` on holds is diagnostic and write-only; no index or reader should be assumed.
- **Replacing a recovery mechanism against a live database needs an overlap release.** An orphan created before the new mechanism existed has no intent, so removing the old mechanism in the same release strands it. Backfilling intents from a join across modules is not an alternative. This application has no deployed database yet; once it has one, every such replacement needs the overlap.
- Every new cross-module write is checked against this rule alongside ADR-0003's: if its first cross-module call commits where this module cannot see it, it needs an intent record, because compensation runs only if the process survives to run it.
