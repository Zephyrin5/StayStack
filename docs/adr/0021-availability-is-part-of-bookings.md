# 0021 - Availability is part of Bookings, not a module upstream of it

**Status:** Accepted

Supersedes the cross-module machinery that [ADR-0003](0003-compensating-actions-over-distributed-transactions.md) and [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md) built for the hold/booking pair, and reverses one decision in [ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md). Neither of the first two is withdrawn: both still govern the boundaries that remain.

## Context

Availability owned one entity, `unit_availability_hold`, and Bookings owned `Booking`. Every meaningful operation touched both:

| Operation | Availability | Bookings |
| --- | --- | --- |
| Confirm a checkout | `held → pending_payment` | insert `Booking`, insert/delete `PendingBookingIntent` |
| Payment succeeds | `pending_payment → booked` | `Pending → Confirmed` |
| Payment window lapses | `→ held` | `Pending → Cancelled` |
| Cancel | `→ held` | `→ Cancelled` |

Not one of them was meaningful in half. A released hold with no cancelled booking is a guest holding a confirmation with no room; a cancelled booking with no released hold is a range blocked forever. Because the two lived in different modules with different `DbContext`s, [ADR-0003](0003-compensating-actions-over-distributed-transactions.md) required each pair to be two commits joined by a compensating action, and [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md) added durable intent records so the forward half could be recovered too.

That machinery worked. What it could not do is make an ambiguous commit answerable. A connection lost after a write commits but before the caller learns it did is indistinguishable, to the caller, from a write that never happened - and every compensating pair had a window where the two halves disagreed and only a background job, minutes later, could reconcile them. Four defects found in review were all instances of that single shape, and three of them were unfixable without either a shared transaction or another layer of reconciliation.

The boundary was not paying for itself. It was not enforcing an invariant, and Availability had no independent consumer: [ADR-0004](0004-module-boundaries-via-contracts-projects.md)'s direction rule put it upstream of Bookings, so it could not so much as name a `Booking`, while every question worth asking about a hold ("should this be released?", "is this claim still live?") is answered by the booking's own state. [ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md) had already conceded the point in one direction, putting `PaymentDueAt` and its sweep in Bookings "because the two halves have to move together".

## Decision

**`unit_availability_hold` moves into Bookings. The Availability module and its Contracts project are deleted.**

A hold is not a thing a booking refers to across a boundary; it is the inventory half of the booking's own lifecycle. One module, one `DbContext`, one transaction per state change.

Routes are unchanged - `POST /api/availability/holds` still exists and `AvailabilityGroup` remains - because the public API surface is a separate concern from the internal module layout, and nothing about a URL implies a project structure.

### What this retires

Four two-commit pairs collapse into single transactions:

- **`ConfirmBookingHandler`.** The hold transition and the `PendingBookingIntent` insert now commit together. This is the whole of the ambiguity fix: the intent is present *if and only if* the hold moved, so the existing verify-before-compensate check can answer a lost acknowledgement from the database instead of guessing from an exception type.
- **`ExpireUnpaidBookingsJob`.** `ReleaseHoldAsync` joins the booking transaction, retiring an ordering argument the job used to carry (release first, or a crash strands the range permanently). There is no longer a wrong order to pick.
- **`BookingPaymentConfirmation`.** `MarkHoldPaidAsync` and `booking.Confirm()` run inside the same row lock. [ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md) chose hold-first over booking-first by comparing which crash remainder could be compensated; neither remainder is now reachable.
- **`CancelBookingHandler`.** `ReleaseHoldOutboxMessage` is deleted; the release is a direct call in the cancelling transaction.

`PendingBookingIntent` and `ReconcileOrphanedBookingIntentsJob` **stay**. The promo redemption still crosses into Promotions and still cannot join a Bookings transaction, so the forward half of *that* pair still needs a durable marker. [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md) is narrowed, not withdrawn.

Likewise the outbox: `ReverseRedemptionOutboxMessage` and `ReverseTransactionOutboxMessage` remain, because Promotions and Transactions remain separate modules. [ADR-0003](0003-compensating-actions-over-distributed-transactions.md) still governs every boundary the merge did not remove. What changed is the count of boundaries, not the rule for crossing one.

### Dapper statements must be handed the ambient transaction

The hold statements are Dapper (per [ADR-0014](0014-ef-core-vs-dapper-decision-rule.md): `UPDATE ... RETURNING` under an exclusion constraint is not EF's job). Dapper does not discover an ambient EF transaction. A command issued on an enlisted connection without being given one either fails or - worse - commits independently while the caller believes it is inside their transaction.

`HoldConfirmation` therefore passes `dbContext.Database.CurrentTransaction?.GetDbTransaction()` on every statement. Null when there is no transaction, which leaves standalone callers behaving exactly as before. **This is the single line that makes the merge more than a file move**; without it the code would compile, pass, and silently keep the old two-commit semantics under a new directory.

### The per-client cap counts `pending_payment`

The alternative is to count `'held'` only and rely on rate limit x payment deadline to bound claims. It fails on the second point below.

**The cost is real.** `client_key` is `ClientNetworkKey` - an IP or IPv6 /64 - so a NAT shares one budget. With `MaxActiveHoldsPerClient: 25`, twenty-five live claims behind one address means the twenty-sixth caller gets a 429.

**The alternative bound is weak.** The hold rate limit R and payment deadline D do bound concurrent claims at R × D - but at the configured values (`Holds.PermitLimit: 20` per 60s, `PaymentWindowMinutes: 30`) that ceiling is **20 × 30 = 600 concurrent claimed ranges per IP**, not a small number. Counting `pending_payment` moves the ceiling from 600 to 25.

**And the escape it closes is free to the attacker.** Counting `'held'` alone makes *confirming a checkout itself the way out of the cap* - the transition moved the row out of the counted set while the exclusion constraint went on blocking its range. Hold, confirm, repeat: no account, no card, no rate-limit trip until 600.

What makes the residual sharing acceptable is that these rows are finite: a shared budget filled with claims that expire in at most 30 minutes is a queue, not a lockout. The failure also lands at hold time, with a retryable 429 while the guest is still choosing dates - not at payment time, with money in hand.

The partial index `ix_unit_availability_holds_client_key_active` covers the same statuses; a filter narrower than its query stops covering it, and this query runs inside a `Serializable` transaction where a sequential scan is not merely slower.

## Consequences

- **The migration that hands the table over could not be empty.** The obvious shape - EF scaffolds `CreateTable` for an entity it has not seen, so hand-empty it - is right about `CreateTable` and wrong about the rest. Availability had made four real schema changes of its own (dropped an index, added `client_key` and its partial index, added the status `CHECK`, dropped `holder_token`), and deleting the module deletes that history. A database migrated from empty would have ended up with `holder_token`, no `client_key`, and no `CHECK` - a schema the model no longer describes. The handoff migration replays those four idempotently (`IF EXISTS` / `IF NOT EXISTS`, and a `pg_constraint` lookup for the check): real work on a fresh database, a no-op where Availability's history already ran. **A fresh-database migration is the only place this class of error appears** - every existing environment passes either way.
- **Provider-specific mapping belongs on the `DbContext`, not in an `IEntityTypeConfiguration`.** `StayRange` is `NpgsqlRange<DateOnly>` over `daterange`, and unit tests build `AppBookingsDbContext` on SQLite, where model validation fails outright. A configuration class cannot see the provider; `Database.IsNpgsql()` on the context can. The same shape had just appeared mapping `xmin` as a concurrency token, which makes it a pattern rather than an incident.
- **`IHoldConfirmation` stays `public`.** Making it internal, as its new location suggests, forces `ConfirmBookingHandler`, three jobs and `BookingsOutboxDispatcher` internal with it - types Mediator, TickerQ and DI discover by reflection. Real churn and discovery risk to express something the directory already says.
- **A retried `ConfirmBookingHandler` reads the hold back rather than re-confirming it.** With the intent and the transition in one transaction, `EnableRetryOnFailure` re-running after a committed-but-unacknowledged attempt finds its own intent present - which now *proves* the hold moved. `ConfirmHoldAsync` would fail its `status = 'held'` guard and abandon a confirmation that had in fact succeeded, so `IHoldConfirmation.GetConfirmedHoldAsync` returns the snapshot instead. The previous two-commit shape did not need this, because the lost acknowledgement could only ever cover the intent.
- **`ExpireUnpaidBookingsJob` gained an outbox row and lost a direct call.** `ReverseRedemptionAsync` crosses into Promotions and cannot join the transaction, so it is enqueued with `booking.Cancel()` and dispatched after the commit - matching `CancelBookingHandler`. Dispatching *inside* would let a successful reversal be followed by a rollback, stranding a Promotions write with no Bookings state explaining it.
- **Payment no longer marks a cancelled booking's hold sold.** The cancelled-booking check happens under the row lock and now precedes the hold transition, where it used to follow it. ADR-0020 documented the old behaviour as deliberate ("the hold above is already `booked` in this case, and is left that way"); it was deliberate given the ordering, and the ordering is gone.
- **One review defect was fixed by the merge without being touched.** The expiry job's release-then-cancel window closed the moment the two shared a `DbContext`. That is the argument for the merge in miniature: the defect was not a mistake in the code, it was a property of the boundary.
- **Two defects were deliberately left unfixed before the merge**, because the merge deletes the code they live in. Fixing them first would have meant writing compensations that were about to be deleted, and reviewing them twice.
- The four commit-ambiguity specification tests were written **before** any fix and left failing, so each one's transition from red to green is attributable. One of them turned green during the merge itself, with no code written against it.
