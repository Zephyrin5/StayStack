# 0016 - Trust model for anonymous endpoints

**Status:** Accepted

## Context

Fifteen endpoints call `AllowAnonymous()`. Some are obviously safe (public property browse); at least one - `HoldAvailabilityEndpoint` - held real inventory hostage with no cap of any kind (finding #1 of the review that prompted this ADR: an unauthenticated caller could hold a unit for `[today, today+3650)` in one request, and the exclusion constraint ADR-0010 built would faithfully enforce that decade-long block). No document previously said what an unauthenticated caller is allowed to consume; the hold endpoint ended up with no owner, no range cap, and no rate limit while the auth endpoints got all three, not because of a deliberate risk assessment but because nobody had written one down. This ADR is that assessment, and the layered fix that came out of it.

## Decision

### Inventory

| Endpoint | What it does | Rate-limited? | Notes |
|---|---|---|---|
| `POST /availability/holds` | Creates a hold (DB write) | Yes (`"holds"` policy) | Also: stay-length cap (90 nights), lead-time cap (730 days), and a 25-concurrent-hold cap per client network - see below. The rate limit is not what bounds held inventory; the concurrent-hold cap is. |
| `POST /auth/sign-in`, `/auth/register`, `/auth/refresh-token` | Credential/token issuance | Yes (`"auth"` policy) | |
| `POST /auth/sign-out` | Revokes a refresh token | No | Self-limiting by construction - only ever revokes a token the caller already possesses; there's nothing to abuse by calling it repeatedly with a token that isn't yours. |
| `POST /bookings/{id}/cancel`, `GET /bookings/{id}/manage` | Booking management for guest checkout | Yes (`"auth"` policy) | Gated by a two-path ownership proof (matching `CustomerId`, or a valid management token) independent of the rate limit. |
| `POST /bookings` (confirm) | Creates a booking and (via a redeemed code) can mutate promotion state | Yes (`"auth"` policy) | Found as a gap during this same review (anonymous, a real DB write with financial consequences, previously uncapped) and closed the same way `CancelBookingEndpoint`/`GetBookingForManagementEndpoint`/`InitiateTransactionEndpoint` already were, rather than left as a known gap for later. |
| `POST /transactions/initiate` | Initiates a payment transaction | Yes (`"auth"` policy) | |
| `POST /reviews/stays` | Leaves a review for a completed stay | No | Same two-path ownership proof as booking cancellation; one review per booking is enforced at the database level (409 on a second attempt), which bounds repeated-write abuse independent of a rate limit. |
| `GET /catalog/properties`, `/catalog/properties/{id}`, `/catalog/properties/{id}/price-calendar`, `/reviews/properties/{id}` | Public browse/read | No | Read-only, each wrapped in its own short-TTL cache (see `GetPropertiesHandler`/`GetPriceCalendarHandler`/`GetPropertyByIdHandler`'s own doc comments) - a cache absorbs repeated-request cost far more cheaply than a rate limiter would. |
| `GET /localization/languages` | Static list | No | No user input, no per-request cost. |

### The hold endpoint's layered defense, and what actually bounds it

Three separate mechanisms apply to `HoldAvailabilityEndpoint`, and they are not
equally load-bearing:

1. **`HoldAvailabilityRequestValidator.MaxStayNights` (90) and `HoldAvailabilityHandler.MaxLeadTimeDays` (730).** These bound how much damage *one* hold can do - a single request can no longer lock a decade of a unit's calendar, only a bounded window.
2. **The `"holds"` rate-limit policy**, partitioned by caller IP (correct once `ForwardedHeaders` is processing a real proxy's headers). This bounds how *many requests* one caller can fire in a window. It is **not** what bounds held inventory - see the correction below. Its accepted cost is that an IP is the unit of "one caller", so a NAT'd office shares one 20/min allowance and a burst of honest concurrent traffic from one location can trip it.
3. **`HoldAvailabilityHandler`'s concurrent-hold cap (`MaxActiveHoldsPerClient`, 25).** Counts a client network's *live* holds, across every unit, and rejects with 429 past the limit. This is what actually bounds the "hold out the whole inventory" attack.

#### Correction: a rate limit does not bound held inventory

An earlier revision of this ADR called the rate-limit policy "the only thing
actually bounding the 'hold out the whole inventory' attack" and "the real
backstop against real inventory being blocked." That was wrong, and it is
recorded here rather than quietly edited away, because it is an easy mistake to
repeat.

A fixed-window limiter bounds request *rate*. Holds are not requests: they
persist on their own 15-minute expiry clock and accumulate. At 20 requests per
60 seconds against a 15-minute hold, a single caller reaches roughly **300
concurrent live holds** and stays under the limit indefinitely - each blocking
up to `MaxStayNights` of one unit through the exclusion constraint. One IP can
saturate a 60-unit property's near calendar in about three minutes without ever
being rate limited. Rate governs how fast you reach saturation, not how much you
can hold.

Bounding a *stock* takes a cap on the stock. That is mechanism (3).

#### Why the cap counts by client network, not by the hold-session cookie

The cap was originally 5 concurrent holds per hold-session cookie, and that was
not an enforcement at all. `HoldSessionCookie` mints a token for anyone who
presents none, so the attack was: delete the cookie, get five more holds,
repeat. The cap was keyed on a value the caller supplies. Its comments said so
honestly - "deliberately soft", "sails past this" - but an enforcement
documented as bypassable is still an enforcement that reads as a limit in the
endpoint's 429 contract and in `TooManyActiveHoldsException`, while bounding
nothing.

It now counts by `Api.Security.ClientNetworkKey`, derived from the connection's
peer address, which the caller cannot choose. The cookie keeps only the job it
can do: an ownership handle for a future "release my hold" endpoint.

**Signing the cookie was considered and rejected as ineffective**, not merely
expensive - a different conclusion from the "Alternatives considered" entry
below, which had rejected it as unnecessary. The attack is *minting*, not
*forging*. Data Protection stops a caller crafting an arbitrary token value; it
does nothing about a caller discarding a valid one and being issued another,
which is free and unauthenticated by design. Signing would have left the bypass
intact while making the mechanism look authenticated - strictly worse than the
honest soft cap it would have replaced.

**Accepted costs of keying on the network**, both the same shape as the rate
limiter's:

- **A shared address shares a budget.** A NAT'd office or carrier-grade NAT draws
  from one allowance, which is why the limit is 25 rather than the old 5 - high
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

The cap is a COUNT-then-INSERT against a shared predicate, so it runs under
`IsolationLevel.Serializable` for the same reason
`CreatePricingRuleHandler`/`UpdatePricingRuleHandler` do (see [ADR-0012](0012-single-pricing-rule-entity-with-write-time-overlap-rejection.md)).
Read Committed lets N concurrent holds from one client on N different units all
COUNT before any commits its INSERT;
`HoldAvailabilityConcurrencyTests.Hold_ConcurrentRequestsFromOneClientNetwork_NeverExceedTheCap`
measured exactly that - 9 holds succeeded against a cap of 5 - before
Serializable was applied. That test matters more now than it did under the
cookie: racing the cap is what is left once discarding the key stops working.

`unit_availability_holds.client_key` is cleared when a hold is confirmed
(`HoldConfirmation.ConfirmHoldAsync`). The cap only ever reads live `held` rows,
so a booked hold's copy is dead weight - and it would be a caller's network
address retained on a row that outlives the hold by years. Clearing it bounds
retention to the 15 minutes the cap actually needs. `ReleaseHoldAsync` does not
restore it and does not need to: it resets `hold_expires_at` to now, so the row
is already outside the cap's predicate.

### Account lockout creates a symmetric, accepted abuse surface

Arming Identity's account lockout (`lockoutOnFailure: true` in `SignInHandler`) closes the credential-stuffing gap where failed password attempts never counted toward anything. It also means five bad guesses against a *known* email now locks that real account out for 15 minutes, repeatably, forever, for the cost of one HTTP request every 15 minutes. This is the standard tradeoff and it's the one being made deliberately here, not a side effect discovered later: an attacker who already knows (or guesses) a valid email can deny that user access to their own account indefinitely. `SignInHandler` does not distinguish a locked-out account from a wrong password in its response (see below) specifically so that lockout itself can't be used to *discover* which emails are registered - but denial-of-service against a known email is accepted, not mitigated.

### Why `SignInHandler` doesn't have a distinct "account locked" response

A tempting improvement - telling a locked-out user why they're locked out instead of a generic "invalid credentials" - was deliberately rejected. A distinguishable lockout response is only ever reachable for an account that *exists* (an unregistered email can never be locked out), which makes it an enumeration oracle no matter how well request timing is equalized between the "no such user" and "wrong password" branches. `SignInHandler`'s dummy-password-hash-verification (paying the same cost for a nonexistent email as a real wrong-password attempt) would be silently undone by adding a distinguishable status one layer up. The cost is real - a legitimately locked-out user sees the same generic message as a wrong password for the full 15-minute window - and it's accepted in favor of not reopening the enumeration channel the rest of `SignInHandler` was built to close.

## Alternatives considered

- **Require authentication for holds.** Rejected outright: the endpoint's own purpose is pre-checkout availability-checking for guests who haven't signed in yet (and may never - guest checkout is a first-class path through this app). Forcing sign-in here would break the actual product requirement, not just harden it.
- **A CAPTCHA or proof-of-work challenge on the hold endpoint.** Would meaningfully raise the cost of the "zero out inventory" attack. Not adopted in this pass - it's a larger UX and infrastructure commitment than the stay-length/lead-time/rate-limit combination above, which closes the same hole with tools this codebase already has.
- **Make the hold-session cookie cryptographically bind to the request (e.g. a signed token tying the session to an IP)**, so it couldn't be trivially regenerated. Rejected - see "Why the cap counts by client network" above for the full reasoning. Signing addresses forging, not minting, so it would not have raised the cost of this attack at all. An earlier revision of this entry rejected it on the weaker ground that "the rate limiter already bounds" the attack, which was itself the mistake corrected above. Binding the *cap* to the network, rather than binding the *cookie* to it, gets the property that was wanted without signing or key rotation.

## Consequences

- Any new anonymous endpoint should be added to the inventory table above at the time it's created, with an explicit answer to "what can an unauthenticated caller do here, and how many times per minute" - not left to be discovered later the way both the hold endpoint and the confirm endpoint were.
- "Rate-limited?" is not the same question as "bounded?". A limiter caps requests per window; anything that *persists* past the request - a hold, a lock, a reservation - needs a cap on the outstanding stock as well. The inventory table asks both, and a new endpoint that creates durable state should answer the second explicitly.
- An enforcement keyed on a value the caller supplies is not an enforcement, however honestly its comments describe the weakness. If a limit is worth having, key it on something the caller cannot mint; if it isn't, delete it rather than leaving a mechanism that reads as a control in the endpoint's contract.
- The account-lockout DoS tradeoff should be revisited if this app ever needs a self-service "my account got locked by someone else" recovery path; none exists today beyond waiting out the 15-minute window.
