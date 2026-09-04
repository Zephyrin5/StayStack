# 0020 - A checkout is a claim with a deadline, not a sale

**Status:** Accepted

Amends [ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md) (the exclusion constraint that makes any hold row block its range) and [ADR-0016](0016-trust-model-for-anonymous-endpoints.md) (what an anonymous caller may cost this platform).

## Context

`ConfirmBookingHandler` moved the hold straight from `'held'` to `'booked'` and created the `Booking` as `Pending`, because payment integration does not exist yet. Three facts about the system turned that into an inventory denial-of-service reachable by anyone:

1. **The exclusion constraint carries no status predicate.** `EXCLUDE USING gist ("unit_id" WITH =, "stay_range" WITH &&)` blocks a range for *any* row, whatever its status.
2. **Nothing reclaimed a `'booked'` row.** `ExpiredHoldsSweepJob` deletes `status = 'held' AND hold_expires_at <= now()`. `ReconcileOrphanedBookingIntentsJob` releases holds whose booking was never created - a real `Pending` booking is not an orphan. Neither touches a booked hold, and no job expired ordinary `Pending` bookings.
3. **The per-client hold cap could not see it.** The cap counts `client_key = @ClientKey AND status = 'held' AND hold_expires_at > @Now`. Confirming both moved the row out of `'held'` *and* nulled `client_key`, so a confirmed hold escaped the cap twice over.

`ConfirmBookingEndpoint` is `AllowAnonymous`, rate-limited at 10/60s per IP. So: hold a unit, submit the checkout form, repeat. Each submission converted a 15-minute hold into a permanent block on a unit's calendar, attributable to nobody, released by nothing, without paying. Roughly 14,400 permanently blocked ranges per IP per day, and the rate limit is per-IP so it distributes trivially.

The deeper problem underneath the abuse is a modelling one: `'booked'` meant *a form was submitted*. Nothing in the schema could distinguish a stay someone had paid for from one someone had merely started paying for and abandoned.

## Decision

**Submitting a checkout claims inventory for a bounded time. Only payment sells it.**

```
held ──confirm checkout──▶ pending_payment ──payment succeeds──▶ booked
                                 │
                                 └──deadline passes / cancelled──▶ held (timer reset, swept normally)
```

- `HoldStatuses.PendingPayment` is a new value in `unit_availability_holds.status`. It blocks its range exactly as the other two do - the claim is real inventory, and search must not offer it.
- `Booking.PaymentDueAt` carries the deadline, and `ExpireUnpaidBookingsJob` enforces it: it cancels the booking, releases the hold, and reverses any promo redemption.
- `'booked'` is written only by `IHoldConfirmation.MarkHoldPaidAsync`, called from the payment-confirmation path.

### The deadline lives on the Booking, and so does the sweep

A payment deadline is a Bookings rule; Availability has no way to interpret one. More decisively, the two halves cannot be separated: releasing the hold without cancelling the booking leaves a guest holding a confirmation with no inventory behind it, and cancelling without releasing leaves the range blocked forever. Availability sits *upstream* of Bookings in [ADR-0004](0004-module-boundaries-via-contracts-projects.md)'s module order and cannot cancel a booking, so the module that owns both the deadline and the booking owns the sweep. Its shape - claim the row in a transaction, then make idempotent cross-module calls - is `ReconcileOrphanedBookingIntentsJob`'s, reused rather than reinvented.

### The cap deliberately does *not* count `pending_payment`

Extending the per-client cap to the new state looks like it closes the escape in point 3. It was rejected.

`client_key` is `ClientNetworkKey` - an IP, or an IPv6 /64, and with `KnownProxies` unset it is the proxy's address for everyone behind it. Capping *holds* by that key is tolerable because holds are uncommitted, cheap, and expire in 15 minutes. Capping *bookings in progress* by it means that once 25 unpaid checkouts exist behind one NAT, the 26th guest cannot check out at all.

It also buys nothing. With a payment deadline of D and a hold rate limit of R, no caller can hold more than R × D units at once, and the rate limiter already enforces R. The deadline is what bounds the abuse; the cap would only add a false-negative failure on the commerce path in exchange for a bound that already exists.

### `booked` has a real writer today

`MarkTransactionSucceededHandler` already enqueues `ConfirmBookingPaymentOutboxMessage`, which reaches `IBookingPaymentConfirmation.ConfirmPaymentAsync`, and that endpoint is admin-reachable now. Hooking `pending_payment → booked` into it means the final transition is exercised by integration tests today rather than sitting dead until a payment provider is wired up.

`ConfirmPaymentAsync` marks the **hold paid first**, then confirms the booking under the booking's row lock.

This ordering was originally the other way round, on the reasoning that a crash between the two commits would leave a Confirmed booking whose hold was still `pending_payment` - which the sweep skips, so a paying guest keeps their range - whereas the reverse would leave a sold hold against a still-`Pending` booking for the sweep to cancel. That compared the two crash remainders and missed the more important asymmetry: **only one of the two failures can be compensated.**

Booking-first fails into a Confirmed booking whose inventory has been released to somebody else. Nothing can repair it - the range may already be resold - and because the hold transition threw, the outbox retried forever, each attempt re-reading an already-Confirmed booking and failing identically until it dead-lettered into an hourly sweep. Money taken, nothing sold, loud rather than fixed.

Hold-first cannot produce that. If the hold transition fails, nothing has committed and the booking is untouched. If the booking confirm then fails, a retry finishes the job, because `MarkHoldPaidAsync` accepts an already-`booked` hold - idempotency that cross-module commit ambiguity makes mandatory, not optional. The only remainder is a sold hold against a still-`Pending` booking, and that is recoverable from both directions: the outbox retry completes it, or the payment window lapses and `ExpireUnpaidBookingsJob` cancels the booking and releases the hold, which drives `ConfirmPaymentAsync` to return `false` and the caller to refund. A compensated outcome instead of an uncompensatable one.

A hold that is gone entirely by the time payment lands now returns `false` rather than throwing, for the same reason: the answer to "this payment cannot be turned into a stay" is a refund, which the caller already does for the cancelled-booking case. Throwing only retried an outcome that could never improve.

### Payment and expiry arbitrate for the booking row the same way

Ordering fixes the crash window but not concurrency, and the two are separate problems.

`ExpireUnpaidBookingsJob` locks the booking with `FOR UPDATE SKIP LOCKED` and re-checks its status under that lock. Payment originally did an unlocked read, a status check against that stale read, and an unconditional EF update - and `Booking` carries no concurrency token, so the generated `UPDATE` keyed on `Id` alone and could not fail. One side of the transition participated in the protocol and the other did not, which is all a lost update needs: payment reads `Pending`; expiry locks the row, releases the hold, commits `Cancelled`; payment's `UPDATE`, which had been blocking on that lock, proceeds and overwrites `Cancelled` back to `Confirmed`. A Confirmed booking whose inventory was just handed back.

`ConfirmPaymentAsync` now transitions under the row lock and re-reads inside it, so it only ever moves `Pending → Confirmed` against committed state. It takes `FOR UPDATE` rather than `SKIP LOCKED`: the needs are opposite - a sweep should step over a row someone else holds and revisit it next run, while a payment in hand must wait to observe the committed outcome, which is exactly how it learns a refund is owed.

## Consequences

- **Every status literal had to be re-examined, and one was a trap.** The status is a plain `varchar` compared in raw SQL, EF expressions and partial-index filters; nothing type-checks it. `ReleaseHoldAsync` matched `WHERE status = 'booked'`. Once checkout produced `pending_payment`, that predicate would have matched nothing - silently turning *every* compensating release (both of `ConfirmBookingHandler`'s catch blocks, its promo-rejection branch, `ReconcileOrphanedBookingIntentsJob`, `CancelBookingHandler`'s outbox message, and the new expiry job) into a zero-row no-op that strands the hold. It now matches both post-checkout states, and has its own regression test because nothing about that failure is loud.
- `UnitAvailabilityLookup`'s three predicates now treat `pending_payment` as blocking. Missing them would have shown claimed units as free in search and on the price calendar, with the exclusion constraint refusing the hold at the last step - the worst version of the bug, since the guest only discovers it after choosing dates.
- **The two partial indexes keep their `status = 'held'` filters.** Both back queries that remain `'held'`-only - the cap and the expiry sweep - so widening them would have added write cost for nothing.
- `HoldStatuses` replaces the literals, and the migration adds `CHECK (status IN ('held', 'pending_payment', 'booked'))`. The constraint cannot catch a stale *read* predicate, but it catches a stale *write* and states the closed set in the schema.
- **No hold rows were backfilled**, though the obvious move was to convert existing `'booked'` rows to `pending_payment`. That would be wrong for any booking genuinely paid for (Confirmed via the admin path): those would sit in `pending_payment` permanently, since the sweep only considers `Pending` bookings. Deciding per row needs the booking's status, which lives in another module's table. It is also unnecessary - the Bookings migration gives every `Pending` booking a deadline, and `ReleaseHoldAsync` matches `'booked'` too, so the stale rows are reclaimed by the ordinary sweep driven by the booking's own state.
- Existing `Pending` bookings are backfilled to `now() + 30 minutes` rather than `created_at + 30 minutes`, so everything gets a full window from deployment instead of being overdue the instant the migration runs. That matters for exactly one row - a checkout in flight as it applies.
- **Migration order between the two modules does not matter**, which is worth stating because it usually would. Availability-first leaves `pending_payment` holds against bookings with no deadline yet: the sweep skips them, no worse than before, corrected when the second migration lands. Bookings-first leaves deadlines against holds still `'booked'`: the sweep releases them correctly, because `ReleaseHoldAsync` matches that state too.
- `ExpireUnpaidBookingsJob` claims its row with a bare `SELECT id ... FOR UPDATE SKIP LOCKED` rather than `FromSqlRaw` as its sibling job does. `Booking` carries `Money` as a complex property and EF composes its own projection over a raw query, asking for a `TotalPrice_Amount` column the snake_case convention never produced.
- **Abandoned checkouts are now an ordinary, visible event** (`bookings.unpaid_bookings.expired`). Unlike the orphaned-intent counters this is not expected to be near zero - the shape is what matters, since a sharp rise means either payment is failing for real guests or someone is cycling checkouts to hold inventory.
- A guest who abandons checkout and returns after the window finds their unit released, possibly to someone else. That is the intended trade and the same one every booking platform makes.
