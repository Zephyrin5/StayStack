# 0016 - Trust model for anonymous endpoints

**Status:** Accepted

## Context

Sixteen endpoints call `AllowAnonymous()`. Some are plainly safe (public property browse). Others write durable state an unauthenticated caller could exhaust: a hold blocks real inventory through the exclusion constraint ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)), so one uncapped request for `[today, today+3650)` would block a unit for a decade. This ADR records, per endpoint, what an unauthenticated caller may consume and what bounds it.

## Decision

### Inventory

| Endpoint | What it does | Rate-limited? | Notes |
|---|---|---|---|
| `POST /availability/holds` | Creates a hold (DB write) | Yes (`"holds"` policy) | Also: stay-length cap (90 nights), lead-time cap (730 days), and a 25-concurrent-hold cap per client network - see below. The rate limit is not what bounds held inventory; the concurrent-hold cap is. |
| `POST /auth/sign-in`, `/auth/register`, `/auth/refresh-token` | Credential/token issuance | Yes (`"auth"` policy) | |
| `POST /auth/sign-out` | Revokes a refresh token | No | Self-limiting by construction - only ever revokes a token the caller already possesses; there's nothing to abuse by calling it repeatedly with a token that isn't yours. |
| `POST /bookings/{id}/manage/session` | Exchanges a management token for a booking session | Yes (`"auth"` policy) | The only endpoint that takes the long-lived token ([ADR-0023](0023-booking-management-sessions.md)). |
| `POST /bookings/{id}/cancel`, `GET /bookings/{id}/manage` | Booking management for guest checkout | Yes (`"auth"` policy) | Gated by a two-path ownership proof (matching `CustomerId`, or a booking session) independent of the rate limit. Cancelling with a session also requires the guest email. |
| `POST /bookings` (confirm) | Creates a booking and (via a redeemed code) can mutate promotion state | Yes (`"auth"` policy) | A claim with a payment deadline, counted against the hold cap ([ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md)). |
| `POST /transactions/initiate` | Initiates a payment transaction | Yes (`"auth"` policy) | Same two-path ownership proof as booking cancellation - see below. |
| `POST /reviews/stays` | Leaves a review for a completed stay | No | Same two-path ownership proof as booking cancellation; one review per booking is enforced at the database level (409 on a second attempt), which bounds repeated-write abuse independent of a rate limit. |
| `GET /catalog/properties`, `/catalog/properties/{id}`, `/catalog/properties/{id}/price-calendar`, `/reviews/properties/{id}` | Public browse/read | Yes (`"reads"` policy) | Each is wrapped in its own short-TTL cache. A cache absorbs the cost of a *repeated* request, not of a stream of *distinct* ones: every miss still pays for a cross-module availability call, a pricing-rule load and, for the calendar, a `generate_series` cross join. The per-request cost is bounded elsewhere (the stay-window caps, the calendar's date bounds, `MaxOffset`, the cache's payload and size limits); the `"reads"` policy is what bounds the rate. Deliberately far looser than `"auth"` or `"holds"` - see `ReadRateLimitOptions` for why a tight limit here would stop guests browsing rather than stop abuse. |
| `GET /localization/languages` | Static list | No | No user input, no per-request cost. |

### The hold endpoint's layered defense, and what actually bounds it

Three separate mechanisms apply to `HoldAvailabilityEndpoint`, and they are not
equally load-bearing:

1. **`StaySearchPolicyOptions.MaxStayNights` (90) and `.MaxLeadTimeDays` (730),** enforced on the hold path by `HoldAvailabilityRequestValidator` and `HoldAvailabilityHandler` respectively. These bound how much damage *one* hold can do: a bounded window, not a decade. Search applies the same values, or it would offer stays that cannot be held; they live in `Catalog.Contracts` so both modules read one value without inverting the module order.
2. **The `"holds"` rate-limit policy**, partitioned by caller IP (correct once `ForwardedHeaders` is processing a real proxy's headers). This bounds how *many requests* one caller can fire in a window. It is **not** what bounds held inventory - see below. Its accepted cost is that an IP is the unit of "one caller", so a NAT'd office shares one 20/min allowance and a burst of honest concurrent traffic from one location can trip it.
3. **`HoldAvailabilityHandler`'s concurrent-hold cap (`MaxActiveHoldsPerClient`, 25).** Counts a client network's *live* holds, across every unit, and rejects with 429 past the limit. This is what actually bounds the "hold out the whole inventory" attack.

#### A rate limit does not bound held inventory

It is easy to read the rate limit as the backstop against held inventory. It is not. A fixed-window limiter bounds request *rate*. Holds are not requests: they
persist on their own 15-minute expiry clock and accumulate. At 20 requests per
60 seconds against a 15-minute hold, a single caller reaches roughly **300
concurrent live holds** and stays under the limit indefinitely - each blocking
up to `MaxStayNights` of one unit through the exclusion constraint. One IP can
saturate a 60-unit property's near calendar in about three minutes without ever
being rate limited. Rate governs how fast you reach saturation, not how much you
can hold.

Bounding a *stock* takes a cap on the stock. That is mechanism (3).

#### Why the cap counts by client network

The cap counts by `Api.Security.ClientNetworkKey`, derived from the connection's peer address, which the caller cannot choose. A key the caller supplies bounds nothing: a hold-session cookie minted for anyone who presents none lets a caller discard it and receive a fresh budget.

**Signing such a cookie is rejected as ineffective.** The attack is *minting*, not *forging*: Data Protection stops a caller crafting a token value, not discarding a valid one and being issued another, which is free and unauthenticated by design. Holds carry no per-browser token at all; if hold ownership is ever built, it gets a token minted for that purpose with a lifetime chosen for it.

**Accepted costs of keying on the network**, both the same shape as the rate
limiter's:

- **A shared address shares a budget.** A NAT'd office or carrier-grade NAT draws
  from one allowance, which is why the limit is 25 - high
  enough that ordinary shared-connection browsing does not reach it, low enough
  that saturating a property's calendar does. `MaxActiveHoldsPerClient` is
  configuration, not a constant, so it can be tuned without a deploy.
- **IPv6 is masked to the /64**, since a single customer is normally allocated at
  least that much and keying on the full 128-bit address would make the cap free
  to bypass. The `"holds"` and `"auth"` rate-limit partitions still key on the
  full address and carry this gap; it matters far less there, but it is the same
  gap.
- **An unattributable request** (null `RemoteIpAddress`) falls back to a single
  shared `"unknown"` partition rather than getting a private budget.

**What is still not bounded:** an attacker distributing across many networks. No
per-caller control can address that, and neither this cap nor the rate limiter
claims to - it is the case a CAPTCHA or proof-of-work challenge would cover (see
"Alternatives considered").

#### Isolation, and retention

The cap is a COUNT-then-INSERT against a shared predicate across units, so the hold transaction runs under `IsolationLevel.Serializable`; the per-unit `UnitAvailabilityLock` does not cover holds from one client on different units. Under Read Committed, N concurrent holds from one client on N units all count before any commits its insert, and `HoldAvailabilityConcurrencyTests.Hold_ConcurrentRequestsFromOneClientNetwork_NeverExceedTheCap` pins the bound.

`unit_availability_holds.client_key` is kept while the hold is `held` or `pending_payment`, which the cap counts, and cleared when the hold becomes `booked` (`HoldConfirmation.MarkHoldPaidAsync`). A booked row outlives the hold by years, and a network address on it would serve no query. `ReleaseHoldAsync` does not restore it: it resets `hold_expires_at` to now, which puts the row outside the cap's predicate.

### `POST /transactions/initiate` requires ownership proof, not just a rate limit

Initiation goes through `IBookingLookup.VerifyBookingAccessAsync`, the same two-path check as cancellation and management, which does not distinguish "doesn't exist" from "isn't yours". Without it, possession of a booking id - which the guest's own client puts in a URL - would be the only credential, and two things would follow:

- **A payment-denial vector.** Anyone holding the id could open a `Pending` transaction, and `ix_transactions_booking_id_active` would reject the real guest's payment with 409.
- **A status oracle.** 404 for an unknown booking and 409 for an unpayable one, to anyone - what `HostAuthorization.RequireOwnership` returns 404 instead of 403 to avoid.

`BookingAccessResult` carries `TotalPrice` and `IsPending`, so the facts a payment needs travel on the result that proves access.

**The 404/409 distinction is kept.** It is an oracle only when anyone can ask. A caller who has proven the booking is theirs is entitled to know why it cannot be paid for, and "not found" about a booking they are looking at would mislead.

### Account lockout creates a symmetric, accepted abuse surface

Identity's account lockout (`lockoutOnFailure: true` in `SignInHandler`) counts failed password attempts, bounding credential stuffing per account. It also means five bad guesses against a *known* email lock that real account out for 15 minutes, repeatably, forever, for the cost of one HTTP request every 15 minutes. This is the standard tradeoff, made deliberately: an attacker who already knows (or guesses) a valid email can deny that user access to their own account indefinitely. `SignInHandler` does not distinguish a locked-out account from a wrong password in its response (see below) specifically so that lockout itself can't be used to *discover* which emails are registered - but denial-of-service against a known email is accepted, not mitigated.

### Why `SignInHandler` doesn't have a distinct "account locked" response

A locked-out user is not told why; they get the generic "invalid credentials". A distinguishable lockout response is only ever reachable for an account that *exists* (an unregistered email can never be locked out), which makes it an enumeration oracle no matter how well request timing is equalized between the "no such user" and "wrong password" branches. `SignInHandler`'s dummy-password-hash-verification (paying the same cost for a nonexistent email as a real wrong-password attempt) would be silently undone by adding a distinguishable status one layer up. The cost is real - a legitimately locked-out user sees the same generic message as a wrong password for the full 15-minute window - and it's accepted in favor of not reopening the enumeration channel the rest of `SignInHandler` was built to close.

## Alternatives considered

- **Require authentication for holds.** Rejected outright: the endpoint's own purpose is pre-checkout availability-checking for guests who haven't signed in yet (and may never - guest checkout is a first-class path through this app). Forcing sign-in here would break the actual product requirement, not just harden it.
- **A CAPTCHA or proof-of-work challenge on the hold endpoint.** Would meaningfully raise the cost of the "zero out inventory" attack. Not adopted: it is a larger UX and infrastructure commitment than the stay-length/lead-time/rate-limit combination above, which closes the same hole with tools this codebase already has.
- **A signed hold-session cookie bound to the request.** Rejected: signing addresses forging, not minting, so it does not raise the cost of discarding a cookie and receiving a new one. Binding the *cap* to the client network gets the wanted property without a cookie, signing or key rotation.

## Consequences

- Any new anonymous endpoint should be added to the inventory table above when it is created, with an explicit answer to "what can an unauthenticated caller do here, and how many times per minute".
- "Rate-limited?" is not the same question as "bounded?". A limiter caps requests per window; anything that *persists* past the request - a hold, a lock, a reservation - needs a cap on the outstanding stock as well. The inventory table asks both, and a new endpoint that creates durable state should answer the second explicitly.
- An enforcement keyed on a value the caller supplies is not an enforcement, however honestly its comments describe the weakness. If a limit is worth having, key it on something the caller cannot mint; if it isn't, delete it rather than leaving a mechanism that reads as a control in the endpoint's contract.
- The account-lockout DoS tradeoff should be revisited if this app ever needs a self-service "my account got locked by someone else" recovery path; none exists today beyond waiting out the 15-minute window.
