# Transaction ownership refactor

Status: **implemented** (Stages 0-7). The decision is [ADR-0003](../adr/0003-cross-module-writes-commit-in-one-transaction.md); the mechanism is [ADR-0029](../adr/0029-atomic-scopes-for-cross-module-work.md); the remaining cross-module coupling is listed in [docs/extraction-inventory.md](../extraction-inventory.md). This document keeps the inventory and measurements the decision was made from.

## Measurements

Harness: `tests/IntegrationTests/Measurements` (skipped unless `STAYSTACK_MEASURE` names an output directory; `STAYSTACK_IDLE_TX_PROBE` names a file for the idle-in-transaction probe).

**Peak pooled connections per operation**

| Operation | Stage 0 | Stage 6 |
|---|---|---|
| Confirm, no promo code | 1 | 1 |
| Confirm, promo code | 1 | 1 |
| Payment success | 2 | 1 |
| Cancel, paid, promo code | 2 | 1 |
| Cancel, unpaid | 2 | 1 |
| Bookings / Transactions relay tick | 2 / 2 | deleted |
| Payment initiation | - | 1 |
| Hold | - | 2 (Bookings + Catalog: the deliberate residual) |
| Expiry tick, refund sweep tick | - | 1, 1 |
| Delete unit, delete property | - | 1, 1 |

Every 2 at Stage 0 was a transaction held while a second module's connection was opened.

**20 concurrent requests at `MaxPoolSize=5`**

| Burst | Stage 0 (with relay loops) | Stage 6 (two runs) |
|---|---|---|
| Payment successes | stalled 15 / 31 / 46 s on pool acquisition timeouts; all 200, but 10 dispatches failed on pool exhaustion and 7 were still pending | 297 ms, 217 ms; 20/20 OK |
| Confirms, half with a promo code | 123 ms | 449 ms, 157 ms; 20/20 OK |

Single runs; the confirm spread is noise-sized rather than a trend.

**Longest idle-in-transaction over the full integration suite:** Stage 0 1,984 ms, Stage 6 1,994 ms - both from tests that hold a transaction open behind a deliberate barrier. Stage 0's outbox claims (191 ms, 60 ms; 15,019 ms under the pool-5 burst) went with the outbox.

## Transaction ownership inventory

What each transaction site became. "Calls out while holding" meant it resolved another module's context, and so a second pooled connection, while its own transaction was open.

| Site (Stage 0) | Called out while holding | Became |
|---|---|---|
| `OutboxDispatcherBase` claim | yes, every handler | deleted (Stage 6) |
| `ConfirmBookingHandler` | no (between its transactions) | scope owner: Bookings; Promotions, Catalog (Stage 4) |
| `PromotionRedemption.RedeemAsync` | no | participant (Stage 4) |
| `PromotionRedemption.ReverseRedemptionAsync` | inside an outbox claim | participant (Stage 5) |
| `CancelBookingHandler` | no; dispatched after commit | scope owner: Bookings; Transactions, Promotions (Stage 5) |
| `ExpireUnpaidBookingsJob` | yes, the payment lookup under the row lock | scope owner: Bookings; Promotions (Stage 5). The payment lookup was later deleted: payment success confirms the booking in the same commit, so a booking still `Pending` under the lock has no succeeded payment |
| `BookingPaymentConfirmation` | no | participant (Stage 5) |
| `MarkTransactionSucceededHandler` | save plus outbox row, then dispatch | scope owner: Transactions; Bookings (Stage 5) |
| `TransactionReversal` | two separate commits | participant; owned by `ResolveOutstandingRefundsJob`'s scope in the sweep (Stage 5) |
| `ReconcileOrphanedBookingIntentsJob`, `ReconcileOrphanedHostLinkIntentsJob` | no; dispatched after | deleted (Stages 4, 3) |
| `BecomeHostHandler` | five independent commits | scope owner: Identity; Hosts (Stage 3) |
| `HostRegistrar.RegisterHostAsync` | bare save | participant (Stage 3) |
| `InitiateTransactionHandler` | yes, the booking re-read under `BookingPaymentLock` | scope owner: Transactions; Bookings read-only (Stage 5) |
| `DeleteUnitHandler`, `DeletePropertyHandler` | yes, archival guard reads under the unit locks | scope owner: Catalog; Bookings read-only (Stage 5) |
| `HoldAvailabilityHandler` | yes, the unit re-read under the unit lock | unchanged: Serializable, see ADR-0029 |
| `CreateUnitHandler`, pricing rule handlers, `RefreshTokenHandler`, `SignUpHandler` | no | unchanged (single module) |
| `HoldConfirmation`, `UnitArchival` (ambient) | - | unchanged; under a scope, `CurrentTransaction` is the owner's |

## Recovery states

Before: 22. Confirm (8: hold moved with an intent, with or without a committed redemption; compensation saved before the intent was discarded; intent reconciled mid-request; lost acknowledgement; incomplete idempotency record; `hold_id` and `key_hash` collisions), outbox (3: pending, dead-lettered, handler effect committed under a rolled-back claim), become host (5), cancel (3), payment (2), expiry (1).

After: each workflow's own lost acknowledgement, recovered by an identity minted before the scope; and a payment succeeding after the cancellation or expiry that owes its refund committed, resolved from the durable `RefundObligation`. `key_hash` replay remains, as a feature.

Independently committed steps per workflow: confirm with a code 3 (up to 6 on failure) → 1; become host 5 → 1; cancel, paid with a code, 6 → 1; payment success 3-5 → 1; expiry 3 → 1.

## Decisions taken in Stage 1

- **D1. Re-point the existing scoped contexts; no second registration.** With a loud diagnostic naming a participant that is not idle, no pooled registrations, and release on the failure path, each tested.
- **D2. Flags at the call site, resolved in Infrastructure; an explicit single-flag owner.** Participation is not enlisted for every module.
- **D3. The unreachable "still in progress" replay branch is deleted**; the `completed_at` column stays nullable, noted in the extraction inventory.
- **D4. Initiation and both archival handlers participate read-only, in one commit.** None needed a logic change. `HoldAvailabilityHandler` stays outside: joining would widen the Serializable envelope over a cross-module read and change retry characteristics on the most contended path. It still shows the amplification, as expected.
- **D5. `ResolveOutstandingRefundsJob` runs each resolution in a scope.** No provider call may run inside it (stated at the call site); `ResolvedAt` means the decision is recorded locally.
- **D6. Booking row before transaction row on every path.** Payment success saves its transaction only after `ConfirmPaymentAsync` has locked the booking; `RefundDeterminismTests` pins a cancellation and a payment racing in each order. ADR-0028 carries the derivation.
- **D7. `RedeemAsync`'s committed-attempt lookup is deleted**; its redemption id now comes from the caller.

Commit-fault injection was verified compatible in Stage 2: a fault on the owner's context fires on the scope's commit; a fault on a borrower's never fires.
