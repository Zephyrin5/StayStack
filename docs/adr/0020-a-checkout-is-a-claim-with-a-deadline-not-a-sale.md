# 0020 - A checkout is a claim with a deadline, not a sale

**Status:** Accepted

## Context

Confirming a checkout is anonymous (`ConfirmBookingEndpoint` is `AllowAnonymous`, rate-limited per IP), and no payment provider is integrated yet. Three facts make whatever state a confirmed checkout leaves behind a question of inventory safety:

1. **The exclusion constraint carries no status predicate** ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)). Any hold row blocks its range.
2. **A sold hold is permanent.** Nothing should reclaim a range someone paid for.
3. **The per-client hold cap bounds live holds** ([ADR-0016](0016-trust-model-for-anonymous-endpoints.md)), and a state outside its predicate escapes it.

If submitting the checkout form produced a sold hold, anyone could hold a unit, submit the form, and repeat - permanently blocking calendars without paying, with nothing in the schema to tell those rows from paid stays.

## Decision

**Submitting a checkout claims inventory for a bounded time. Only payment sells it.**

```
held ──confirm checkout──▶ pending_payment ──payment succeeds──▶ booked
                                 │
                                 └──deadline passes / cancelled──▶ held (timer reset, swept normally)
```

- `pending_payment` blocks its range exactly as the other statuses do: the claim is real inventory, and search must not offer it.
- `Booking.PaymentDueAt` carries the deadline, and `ExpireUnpaidBookingsJob` enforces it: it cancels the booking, releases the hold, writes a refund obligation ([ADR-0027](0027-refunds-are-decided-once-from-a-durable-obligation.md)), and reverses any promo redemption.
- `booked` is written only by `IHoldConfirmation.MarkHoldPaidAsync`, from the payment-confirmation path.
- A `pending_payment` claim counts against the client's hold cap ([ADR-0021](0021-availability-is-part-of-bookings.md)).

The deadline and the sweep belong to Bookings, which owns both the booking and the hold: releasing one without cancelling the other is never correct.

### Payment confirmation

`MarkTransactionSucceededHandler` enqueues `ConfirmBookingPaymentOutboxMessage`, which reaches `IBookingPaymentConfirmation.ConfirmPaymentAsync`. That endpoint is admin-reachable today, so the final transition is exercised by integration tests before a provider exists.

`ConfirmPaymentAsync` runs one transaction: lock the booking row, return `false` if the booking is cancelled, mark the hold paid, confirm the booking, commit. A booking is never confirmed with its inventory released, nor a hold sold against a booking left `Pending`. `MarkHoldPaidAsync` accepts an already-`booked` hold, so a redelivered message succeeds.

A hold released or expired before payment lands also returns `false`: a payment that cannot become a stay is refunded, and throwing would only retry an outcome that cannot improve.

### Payment and expiry arbitrate for the booking row

`ExpireUnpaidBookingsJob` locks the booking with `FOR UPDATE SKIP LOCKED` and re-checks its status under the lock; `ConfirmPaymentAsync` takes `FOR UPDATE` and re-reads inside it. `Booking` carries no concurrency token, so an unlocked read followed by an update would overwrite a committed `Cancelled` with `Confirmed`. The modes differ because the needs do: a sweep steps over a row someone else holds and revisits it next run, while a payment in hand must wait to observe the committed outcome, which is how it learns a refund is owed. Lock order with `BookingPaymentLock` is [ADR-0028](0028-advisory-locks-and-lock-order.md).

Before expiring, the job asks `ITransactionLookup.HasSucceededPaymentAsync`: a payment whose confirmation message has not been delivered leaves a paid booking `Pending` and overdue, and must not be expired.

## Alternatives considered

- **Submitting a checkout sells the hold.** Rejected: it makes anonymous form submissions permanent inventory.
- **A deadline on the hold, swept independently of the booking.** Rejected: releasing a hold without cancelling its booking leaves a confirmation with no room.
- **Bounding claims by rate limit × payment window instead of the cap.** Rejected; see [ADR-0021](0021-availability-is-part-of-bookings.md).
- **"Succeeded before the deadline" as the expiry guard.** Rejected: no gateway event time is recorded, only when this system noticed, so it would cancel payments made in time and reported late.

## Consequences

- **Hold status is a plain `varchar` compared in raw SQL, EF expressions and partial-index filters**, and nothing type-checks a predicate. `HoldStatuses` holds the literals, and `CHECK (status IN ('held', 'pending_payment', 'booked'))` states the closed set, catching a stale write but not a stale read. A new status means re-examining every predicate.
- **`ReleaseHoldAsync` matches `pending_payment` and `booked`.** Every caller is a compensation or cancellation acting on a post-checkout hold; matching one state alone would make them silent zero-row no-ops that strand the hold. The cancellation and expiry tests release `pending_payment` holds through it.
- **`UnitAvailabilityLookup` treats `pending_payment` as blocking** in all three predicates, so a claimed unit never appears free in search or on the price calendar only to be refused at hold time.
- **The expiry sweep's partial index filters `status = 'held'`**, matching its query; the cap's client-key index covers `held` and `pending_payment`, matching its query.
- `ExpireUnpaidBookingsJob` claims its row with a bare `SELECT id ... FOR UPDATE SKIP LOCKED` and then loads through EF: `FromSqlRaw` over `SELECT *` fails for `Booking`, whose `Money` complex property makes EF ask for a `TotalPrice_Amount` column the snake_case convention never produces.
- **Abandoned checkouts are an ordinary, visible event** (`bookings.unpaid_bookings.expired`). A sharp rise means payment is failing for real guests, or someone is cycling checkouts to hold inventory.
- A guest who returns after the window finds the unit released, possibly to someone else. That is the intended trade.
