# 0022 - Checkout idempotency keys

**Status:** Accepted

Builds on [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md) (the durable-marker shape this reuses) and [ADR-0021](0021-availability-is-part-of-bookings.md) (which made the transaction this row commits inside possible). Keeps the hash-only rule that governs [ADR-0009](0009-refresh-token-rotation-with-family-reuse-detection.md)-style bearer credentials - see the amendment at the end, which is where this ADR first broke it and then took it back.

## Context

`POST /api/bookings` had no way to be retried.

A guest checking out anonymously receives a management token in the response. It is generated in memory by `SecureToken.Generate()`; only `SecureToken.Hash` of it is persisted, in `booking_management_tokens.token_hash`. The booking id is likewise first seen by the client in that response.

So a connection dropped after the commit is not a partial failure - it is a total one, for the guest:

- They hold a real booking, blocking a real range.
- They do not know its id.
- They do not have the token that would let them view, manage, or cancel it.
- They cannot be looked up by email, because guest checkout creates no account.
- Retrying does not help. The hold has already moved to `'pending_payment'`, so the retry fails `ConfirmHoldAsync`'s `status = 'held'` guard and returns **404** - the same answer as a hold that never existed.

The booking then sits until `ExpireUnpaidBookingsJob` cancels it at the payment deadline. Inventory recovers; the guest's checkout does not. Nobody is charged today, and that is precisely the property that stops holding the moment payment is wired up: the same dropped connection would then take money for a booking its payer cannot reach.

The durable-intent machinery does not help here. It exists so the *system* can recover a half-finished cross-module write. This is the opposite problem: the write finished perfectly, and the *client* lost the answer.

## Decision

**A client may send an `Idempotency-Key` header. The same key, with the same request, replays the original response.**

```
POST /api/bookings
Idempotency-Key: 3f1a...   (16-128 chars; a UUID is ideal)
```

A new table, `checkout_idempotency_records`, keyed by the pre-generated booking id - deliberately the same shape as `pending_booking_intents`, for the same reasons ([ADR-0017](0017-durable-intent-records-for-cross-module-writes.md)): a row written before the work, resolved by the transaction that finishes it.

The endings differ, and that difference is the whole design. An intent is *deleted* on success, because it carries nothing `Booking` does not already record. This row *survives*, because it carries the one thing that exists nowhere else once a response is lost.

### The reservation is taken in the first transaction, not the last

Written alongside the `PendingBookingIntent` and the hold transition, inside Transaction A. Three properties follow, and none of them hold if the record is written on the way out:

- **Concurrent duplicates are separated before a hold moves.** Two requests with one key both miss the replay read - it happens before either writes - so what separates them is the unique index on `key_hash` failing for the loser, while the hold is still `'held'`. Written at the end, the loser would instead fail on the consumed hold and report 404 for a checkout that had succeeded.
- **A rollback frees the key.** A key burned by a failed attempt is worse than no key: the client's retry, the entire reason for sending one, would be refused.
- **The record exists if and only if the confirmation began** - the same present-iff-committed property the intent gained in [ADR-0021](0021-availability-is-part-of-bookings.md).

Completion - `completed_at` and the token - is written in the same `SaveChangesAsync` as the `Booking` insert. Anything later would reintroduce the original bug one level up: a committed booking whose replay record never landed is a guest stranded by the mechanism meant to rescue them.

### Replay re-reads the booking and mints a new token

Strict idempotency replays the original response verbatim. This does not, and the contract is worth stating exactly: **the same booking's current state, and a newly issued capability.**

Everything derivable from committed state is re-read, because all of it *can go stale*: a booking cancelled between the original request and the replay would be reported as `Pending`, and the client would act on that. Reporting settled state is the same choice `CancelBookingHandler`'s recancel branch already makes.

The management token is re-issued rather than returned, because it was never stored. `booking_management_tokens` holds `SecureToken.Hash` and nothing else, so the original plaintext exists only in the response that was lost - there is nothing to hand back.

Nothing requires the replayed token to be *the same* token. `BookingAccessChecker` matches on hash, so several valid tokens work unchanged, and the replay is additive rather than rotating: **the original token stays valid.** That is the deliberate half - a guest who did receive the first response is not locked out by their own client's retry.

The 24-hour window (`BookingLifecyclePolicyOptions.CheckoutReplayWindowHours`) is checked in `ReplayAsync`, on the path that actually hands the credential over. It used to live only in `PurgeReplayedCheckoutsJob`'s `DELETE` - which is to say it existed only as a background job's behaviour: stop that job, break its cron, or let it fail quietly, and replay went on working indefinitely. A window nothing checks is not a window. The purge is cleanup now, not enforcement.

### The fingerprint is what makes a guessed key useless

Replay returns a live management token, so without a second factor a guessed key would be a way into somebody else's booking. `request_fingerprint` is a SHA-256 over the hold id, guest name, email, phone and promo code; a mismatch is refused with a 409 that says nothing about the booking behind the key.

An attacker must therefore already know the hold id and the guest's name and email - at which point the key has told them nothing new. The 16-character floor is secondary, and exists to make a client sending a counter fail loudly rather than discover the problem when two guests collide on `1`.

The key itself is never stored, only `SHA-256` of it, so a database reader cannot replay other people's checkouts either. Unit-separated (``) join before hashing, because `("ab","c")` and `("a","bc")` otherwise hash identically and guest names sit next to emails.

## Consequences

- **The length rule lives in the handler, not `ConfirmBookingRequestValidator`.** FastEndpoints runs validators against the bound request *before* the endpoint body, and the key is assigned in `HandleAsync` (the `HoldAvailabilityRequest.ClientKey` pattern - header-only, non-bindable from body/query/route/form, so no second channel can contradict it). A rule in the validator passed on every request, including the ones it existed to reject. `[FromHeader]` binding was tried first, precisely to keep the rule in the validator and get the header into the OpenAPI document; it does not bind alongside `[JsonIgnore]` here, and the failure is silent - four tests went red with the key simply absent.
- **The header is consequently not an OpenAPI parameter.** It is described in the endpoint's summary instead, and clients must set it explicitly. This is what most idempotency-key clients do anyway, but it is a real cost of the binding decision above rather than a preference.
- **A retry of *our own* attempt is not a replay.** When Transaction A commits and the acknowledgement is lost, the execution strategy re-runs the delegate and *both* unique indexes fire. The recovery distinguishes the two cases by the pre-generated booking id: a different id is somebody else's reservation and gets replayed or refused; our own id falls through to the existing intent recovery and finishes the confirmation. Treating it as a replay would answer our own retry with "still in progress" and strand a confirmation that was not stuck at all until the reconcile job unwound it minutes later.
- **`ReconcileOrphanedBookingIntentsJob` deletes the reservation with the intent**, in the same transaction, so an abandoned confirmation frees its key. Both that delete and the handler's own compensating delete filter on `completed_at IS NULL` - the handler's runs on the verify-before-compensate path where the booking *did* commit, and deleting a completed record there would destroy the replay for exactly the case this feature exists for.
- **Not applied to the other write endpoints.** Cancel is already idempotent by state (a re-cancel is a no-op success), and the hold endpoint is cheap to repeat and self-expiring. Checkout is the one place where a lost response destroys information that exists nowhere else.
- **This does not deduplicate distinct requests.** Two different keys against one hold are two checkouts as far as this table is concerned; the second still fails on the consumed hold, which is the hold's own guarantee and not idempotency's. The failure mode being fixed is not a double booking - the exclusion constraint and the single-use hold already prevent that - it is a guest locked out of a real one.

## Amendment: the plaintext token is gone, and so is the exception

This ADR originally stored the management token in plaintext in `checkout_idempotency_records.management_token`, and argued the cost was bounded: one column, a 24-hour window, a value the client already holds. That argument was wrong about which threat matters.

A leaked *link* exposes one booking. A leaked *backup* - or one read of that table by anyone with database access - yielded live bearer credentials for every anonymous guest checkout inside the window, all at once. `booking_management_tokens` stores only a hash precisely so that a database read cannot produce a working credential, and keeping a plaintext copy beside it undid that property for exactly the population with no account to fall back on. The bound was on how long, never on how many.

The column is dropped (`DropStoredManagementTokenFromCheckoutIdempotency`). Replay mints a fresh token instead, which delivers the same thing the guest actually needs - a working credential for their booking - without a recoverable secret at rest. Encrypting at rest was the other option and buys identical semantics for the price of shared key storage and a rotation story across instances.

Two things followed that were not obvious when the change was made:

- **A unique index on `booking_management_tokens.booking_id` had to go.** Minting on replay failed with `23505`. The index recorded an invariant that was true when it was written - one `ConfirmBookingHandler` call per booking - and replay is precisely the exception to it. Now a plain index (`AllowSeveralManagementTokensPerBooking`).
- **Tokens accumulate.** Each replay adds a row, and nothing removes them. That is acceptable at the scale this operates: replay is rare, it is bounded to a 24-hour window per key, and the rows are small. It is also the reason the window moved into `ReplayAsync` - unbounded replay would have made it unbounded accumulation. Worth a sweep if replay ever becomes routine; not worth a cap, which would have to choose between refusing a legitimate retry and evicting a token the guest is holding.
