# 0022 - Checkout idempotency keys, and the one secret we store in plaintext

**Status:** Accepted

Builds on [ADR-0017](0017-durable-intent-records-for-cross-module-writes.md) (the durable-marker shape this reuses) and [ADR-0021](0021-availability-is-part-of-bookings.md) (which made the transaction this row commits inside possible). Makes a bounded exception to the hash-only rule that governs [ADR-0009](0009-refresh-token-rotation-with-family-reuse-detection.md)-style bearer credentials.

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

### Replay re-reads the booking; only the token is stored

Strict idempotency replays the original response verbatim. This does not, and the reason is that only one field is unrecoverable.

Everything else in the response is derivable from committed state, and *can go stale*: a booking cancelled between the original request and the replay would be reported as `Pending`, and the client would act on that. Reporting settled state is the same choice `CancelBookingHandler`'s recancel branch already makes. It also keeps exactly one secret in the table instead of a copy of the whole response.

### Storing the plaintext token is the cost, and it is bounded

This is the part worth arguing with, so it is stated plainly: `management_token` holds a live credential in plaintext, reversing the rule that `booking_management_tokens` follows.

The alternative was considered and rejected: replay the booking but not the token. That leaves the guest exactly as locked out as before - it implements the feature for authenticated callers, who never needed it, and not for anonymous ones, who are the only people it is for.

What bounds the cost:

- **24 hours** (`CheckoutIdempotencyRecord.ReplayWindow`), enforced by `PurgeReplayedCheckoutsJob`. Unlike most retention sweeps this job is not about table size - it is the only thing that makes the window real, and it runs hourly rather than daily so a token is not readable for up to a day past the window it was promised.
- **One column**, not a serialised response.
- **A value the client already holds in plaintext anyway**, and which travels in plaintext in the original response.
- **Null for authenticated callers**, who get no management token at all.

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
