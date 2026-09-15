# 0022 - Checkout idempotency keys

**Status:** Accepted

Builds on [ADR-0003](0003-cross-module-writes-commit-in-one-transaction.md) (which commits this row with the hold transition and the booking) and [ADR-0021](0021-availability-is-part-of-bookings.md) (which puts the hold in Bookings). Bearer credentials are stored only as hashes, as for [ADR-0009](0009-refresh-token-rotation-with-family-reuse-detection.md)'s refresh tokens.

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

The execution strategy's recovery does not help here either. It recovers a lost acknowledgement *within* one request. This is a different problem: the request finished, and the *client* lost the answer.

## Decision

**A client may send an `Idempotency-Key` header. The same key, with the same request, replays the original response.**

```
POST /api/bookings
Idempotency-Key: 3f1a...   (16-128 chars; a UUID is ideal)
```

A new table, `checkout_idempotency_records`, keyed by the pre-generated booking id. It survives the checkout, because it carries the one thing that exists nowhere else once a response is lost.

### The record commits with the booking

It is written in the confirmation's atomic scope, with the hold transition and the `Booking`. Three properties follow:

- **The record exists if and only if the booking does.** A committed booking whose replay record never landed would be a guest stranded by the mechanism meant to rescue them.
- **A rollback frees the key.** A key burned by a failed attempt is worse than no key: the client's retry, the entire reason for sending one, would be refused.
- **Concurrent duplicates never commit two bookings.** Two requests with one key both miss the replay read - it happens before either writes. For the same hold, the second waits on the hold's row lock, matches nothing once the first commits, and replays its record. For different holds, the unique index on `key_hash` fails for the loser, and its scope rolls its hold transition back.

### Replay re-reads the booking and mints a new token

Strict idempotency replays the original response verbatim. This does not, and the contract is worth stating exactly: **the same booking's current state, and a newly issued capability.**

Everything derivable from committed state is re-read, because all of it *can go stale*: a booking cancelled between the original request and the replay would be reported as `Pending`, and the client would act on that. Reporting settled state is the same choice `CancelBookingHandler`'s recancel branch already makes.

The management token is re-issued rather than returned, because its plaintext is never stored. `booking_management_tokens` holds `SecureToken.Hash` and nothing else, so the original plaintext exists only in the response that was lost. A stored plaintext copy would turn one read of the table, or one leaked backup, into live credentials for every anonymous checkout inside the window; a hash-only table cannot produce a working credential. The replay is minted after the confirmation's transaction is resolved ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)), so its hash row is committed before the guest receives it.

Nothing requires the replayed token to be *the same* token. `BookingAccessChecker` matches on hash, so several valid tokens work unchanged, and the replay is additive rather than rotating: **the original token stays valid.** That is the deliberate half - a guest who did receive the first response is not locked out by their own client's retry.

The 24-hour window (`BookingLifecyclePolicyOptions.CheckoutReplayWindowHours`) is checked in `ReplayAsync`, on the path that hands the credential over. `PurgeReplayedCheckoutsJob` deletes expired records as cleanup; enforcement does not depend on that job running.

### The fingerprint is what makes a guessed key useless

Replay returns a live management token, so without a second factor a guessed key would be a way into somebody else's booking. `request_fingerprint` is a SHA-256 over the hold id, guest name, email, phone and promo code; a mismatch is refused with a 409 that says nothing about the booking behind the key.

An attacker must therefore already know the hold id and the guest's name and email - at which point the key has told them nothing new. The 16-character floor is secondary, and exists to make a client sending a counter fail loudly rather than discover the problem when two guests collide on `1`.

The key itself is never stored, only `SHA-256` of it, so a database reader cannot replay other people's checkouts either. Unit-separated (``) join before hashing, because `("ab","c")` and `("a","bc")` otherwise hash identically and guest names sit next to emails.

## Alternatives considered

- **Store the plaintext token and return it verbatim.** Rejected: a database read or leaked backup yields working credentials for every anonymous checkout inside the window, all at once.
- **Encrypt the stored token.** Rejected: the same semantics as minting a new one, for the price of shared key storage and a rotation story across instances.
- **A reservation row written before the hold moves, completed at the end.** The previous shape, when the confirmation was several commits. With one commit it only adds an in-progress state that no reader can observe.

## Consequences

- **The length rule lives in the handler, not `ConfirmBookingRequestValidator`.** FastEndpoints runs validators against the bound request *before* the endpoint body, and the key is assigned in `HandleAsync` (the `HoldAvailabilityRequest.ClientKey` pattern - header-only, non-bindable from body/query/route/form, so no second channel can contradict it). A rule in the validator passed on every request, including the ones it existed to reject. `[FromHeader]` binding was tried first, precisely to keep the rule in the validator and get the header into the OpenAPI document; it does not bind alongside `[JsonIgnore]` here, and the failure is silent - four tests went red with the key simply absent.
- **The header is consequently not an OpenAPI parameter.** It is described in the endpoint's summary instead, and clients must set it explicitly. This is what most idempotency-key clients do anyway, but it is a real cost of the binding decision above rather than a preference.
- **A retry of *our own* attempt is not a replay.** When the confirmation commits and its acknowledgement is lost, the retried attempt finds its own `Booking` by the pre-generated id before touching the hold, and returns it with the token it already stored. Only a request that finds the hold gone looks the key up and replays.
- **The window and the purge both run from `created_at`**, the instant the record committed with its booking, so a record the request path still replays is never purged.
- **Not applied to the other write endpoints.** Cancel is already idempotent by state (a re-cancel is a no-op success), and the hold endpoint is cheap to repeat and self-expiring. Checkout is the one place where a lost response destroys information that exists nowhere else.
- **This does not deduplicate distinct requests.** Two different keys against one hold are two checkouts as far as this table is concerned; the second still fails on the consumed hold, which is the hold's own guarantee and not idempotency's. The failure mode being fixed is not a double booking - the exclusion constraint and the single-use hold already prevent that - it is a guest locked out of a real one.
- **Several management tokens can exist per booking.** Each replay adds a row, so `booking_management_tokens.booking_id` has a plain index, not a unique one. Tokens accumulate: replay is rare, bounded to the window per key, and the rows are small. A cap would have to choose between refusing a legitimate retry and evicting a token the guest holds; a sweep is the remedy if replay becomes routine.
