# 0027 - Refunds are decided once, from a durable obligation

**Status:** Accepted

## Context

A cancelled booking may be owed a refund, and the amount depends on the order of two events recorded in different modules: when the payment succeeded (Transactions) and when the booking was cancelled (Bookings). Paid then cancelled, the guest gave up a stay and cancellation policy applies. Cancelled then paid - or cancelled by expiry - the payment bought nothing and is refunded in full.

Either event can commit first, outbox delivery can reorder them, and either side can crash between its commit and the next step. A design in which each path decides whether the *other* path will handle the refund has no party that can verify that claim at the moment it makes it: neither can see whether the other still has actionable work.

## Decision

**Cancellation records that a refund may be owed; resolution decides the amount once, when both inputs are committed.**

- **`RefundObligation`** (Bookings), keyed by booking id, written in the same transaction as `Booking.Cancel()` by every cancelling path: `CancelBookingHandler` with the guest-policy amount, `ExpireUnpaidBookingsJob` with the full price and cause `Expiry`. Neither writer needs to know whether a payment exists; an unpaid booking's obligation resolves to nothing.
- **`TransactionReversal.ResolveAsync`** (Transactions) is the single resolver. It reads the booking's transactions, then the obligation through `IBookingLookup`, and records the refund with `MarkRefundPending`.
- **`RefundDecision`** (Bookings.Contracts) is the single owner of the amount: the obligation's amount when the payment succeeded at or before the cancellation, or when `SucceededAt` is unknown; the full payment when it succeeded after. The resolver records from it, and `CancelBookingHandler` reports a pending refund from it, so the two cannot disagree. Guest cancellation policy is applied only when the obligation is written.

**Callers of the resolver**, in order of latency: the `ReverseTransactionOutboxMessage` handler dispatched after a cancellation, the payment-confirmation handler when the booking it confirms is already cancelled (`RefundUnusablePaymentByTransactionAsync`, which also refunds a payment with no obligation behind it), and `ResolveOutstandingRefundsJob`, which retries unresolved obligations with exponential backoff (`NextAttemptAt`, `Attempts`). The messages are latency; the job makes correctness independent of delivery order.

### The transaction's status is the authority; `ResolvedAt` is bookkeeping

The refund (Transactions) and the obligation's `ResolvedAt` marker (Bookings) commit separately, and `OutboxDispatcherBase` runs a handler inside its claim transaction. Which of the two writes is uncommitted therefore depends on the caller:

| Caller | Refund | Marker |
|---|---|---|
| `TransactionsOutboxDispatcher` | inside the claim transaction | commits immediately |
| `BookingsOutboxDispatcher` | commits immediately | inside the claim transaction |

So a marker can exist with no refund behind it, and a refund with no marker. The resolver therefore:

- **never returns early on `ResolvedAt`** - a marker left by a rolled-back claim would otherwise suppress the refund permanently;
- **reads every status a recorded refund can be in** (`RefundPending`, `Refunded`, `RefundFailed`, via `HasRecordedARefund`) and finishes only the bookkeeping when one exists;
- **on a concurrency conflict** (`TransactionAlreadyFinalizedException` from the entity guard, or `DbUpdateConcurrencyException` from the xmin token when two resolvers both loaded `Succeeded`) reloads the transaction and marks the obligation only if a refund now exists.

`MarkRefundObligationResolvedAsync` filters `ResolvedAt IS NULL`, so repeats are no-ops. The general rule: a flag committed in one module never gates work whose completion is recorded in another; read the other module's state, and use the flag only to skip when it agrees.

### A booking may have several payment attempts

`ix_transactions_booking_id_active` constrains `Pending` and `Succeeded` to one at a time and says nothing else, so a `RefundPending` attempt beside a `Succeeded` one is a legal state. A path that knows which attempt it concerns resolves by transaction id - the confirmation message carries it. A booking-wide read takes the first match, `Succeeded` first then newest by id, never `SingleOrDefault`. Ordering is by `Id` (version-7, creation-ordered, never null), not `SucceededAt`, which is null on older rows and which SQLite cannot order.

Initiation takes `BookingPaymentLock` ([ADR-0028](0028-advisory-locks-and-lock-order.md)) so a cancellation cannot commit between its payability check and its insert; otherwise that interleaving produces a payment against a cancelled booking.

### Reporting reads one observation

`CancelBookingHandler` describes the payment from one `GetPaymentStateAsync` read, which returns amount, refund amount, `SucceededAt` and `RefundStatus` together. Two separate reads can straddle a `Succeeded → RefundPending` transition and describe a state that never existed. The response carries `RefundStatus` (`None`, `Pending`, `Refunded`, `Failed`); `RefundPending` is derived from it for existing clients.

## Alternatives considered

- **Each path decides from timestamps whether the other path owns the refund.** Rejected: neither party can verify the other still has actionable work, so an interleaving exists in which both decline and nothing retries.
- **Reading the booking's `CancelledAt` from the resolver instead of the obligation.** Rejected: read through another module before the cancellation commits, it is null and makes a policy refund look like a full one. The obligation is visible if and only if the cancellation committed.
- **Treating `ResolvedAt` as authority.** Rejected for the dispatcher asymmetry above.
- **Ordering the backstop sweep by `CancelledAt`.** Rejected: most cancellations are unpaid and resolve to nothing, so a capped batch ordered by creation stays full of them and never reaches newer obligations with money behind them. Backoff on `NextAttemptAt` moves them aside without marking them resolved - a late payment is exactly what the row waits for.

## Consequences

- `RefundDeterminismTests` covers both event orders, `RefundCommitBoundaryTests` the marker/refund split and the resolver races, `PendingRefundReportingTests` that a reported pending amount equals the recorded one.
- **Open:** `RefundFailed` has no transition out. `MarkRefundPending` keeps the first recorded amount, `MarkRefunded` requires `RefundPending`, and the resolver treats `RefundFailed` as recorded. Nothing retries a failed provider refund, and whether `ResolvedAt` means "decision recorded" or "money moved" is undecided. Both are deferred to payment-gateway integration, when refund failures become routine.
- An existing `Pending` payment succeeding is not excluded by `BookingPaymentLock`; a payment that succeeds as expiry commits is refunded through the obligation expiry wrote.
