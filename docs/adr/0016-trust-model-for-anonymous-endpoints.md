# 0016 - Trust model for anonymous endpoints

**Status:** Accepted

## Context

Fifteen endpoints call `AllowAnonymous()`. Some are obviously safe (public property browse); at least one - `HoldAvailabilityEndpoint` - held real inventory hostage with no cap of any kind (finding #1 of the review that prompted this ADR: an unauthenticated caller could hold a unit for `[today, today+3650)` in one request, and the exclusion constraint ADR-0010 built would faithfully enforce that decade-long block). No document previously said what an unauthenticated caller is allowed to consume; the hold endpoint ended up with no owner, no range cap, and no rate limit while the auth endpoints got all three, not because of a deliberate risk assessment but because nobody had written one down. This ADR is that assessment, and the layered fix that came out of it.

## Decision

### Inventory

| Endpoint | What it does | Rate-limited? | Notes |
|---|---|---|---|
| `POST /catalog/holds` | Creates a hold (DB write) | Yes (`"holds"` policy) | Also: stay-length cap (90 nights), lead-time cap (730 days), 5-active-holds-per-session cap - see below. |
| `POST /auth/sign-in`, `/auth/register`, `/auth/refresh-token` | Credential/token issuance | Yes (`"auth"` policy) | |
| `POST /auth/sign-out` | Revokes a refresh token | No | Self-limiting by construction - only ever revokes a token the caller already possesses; there's nothing to abuse by calling it repeatedly with a token that isn't yours. |
| `POST /bookings/{id}/cancel`, `GET /bookings/{id}/manage` | Booking management for guest checkout | Yes (`"auth"` policy) | Gated by a two-path ownership proof (matching `CustomerId`, or a valid management token) independent of the rate limit. |
| `POST /bookings` (confirm) | Creates a booking and (via a redeemed code) can mutate promotion state | Yes (`"auth"` policy) | Found as a gap during this same review (anonymous, a real DB write with financial consequences, previously uncapped) and closed the same way `CancelBookingEndpoint`/`GetBookingForManagementEndpoint`/`InitiateTransactionEndpoint` already were, rather than left as a known gap for later. |
| `POST /transactions/initiate` | Initiates a payment transaction | Yes (`"auth"` policy) | |
| `POST /reviews/stays` | Leaves a review for a completed stay | No | Same two-path ownership proof as booking cancellation; one review per booking is enforced at the database level (409 on a second attempt), which bounds repeated-write abuse independent of a rate limit. |
| `GET /catalog/properties`, `/catalog/properties/{id}`, `/catalog/properties/{id}/price-calendar`, `/reviews/properties/{id}` | Public browse/read | No | Read-only, each wrapped in its own short-TTL cache (see `GetPropertiesHandler`/`GetPriceCalendarHandler`/`GetPropertyByIdHandler`'s own doc comments) - a cache absorbs repeated-request cost far more cheaply than a rate limiter would. |
| `GET /localization/languages` | Static list | No | No user input, no per-request cost. |

### The hold endpoint's layered defense, and what actually bounds it

Three separate mechanisms now apply to `HoldAvailabilityEndpoint`, and they are not equally load-bearing:

1. **`HoldAvailabilityRequestValidator.MaxStayNights` (90) and `HoldAvailabilityHandler.MaxLeadTimeDays` (730).** These bound how much damage *one* hold can do - a single request can no longer lock a decade of a unit's calendar, only a bounded window.
2. **The `"holds"` rate-limit policy**, partitioned by caller IP (correct once `ForwardedHeaders` is processing a real proxy's headers - see the forwarded-headers fix this same review pass added). This bounds how *many* requests one caller can fire.
3. **The hold-session cookie's per-session cap (5 concurrent active holds).** This is honestly soft: a scripted caller that drops the cookie mints a fresh, unlinked session token on every request and sails straight past it. It does **not** bound the "hold out the whole inventory" attack - (1) and (2) above are what actually do that.

The cookie's real justification is different, and worth stating plainly rather than dressing it up as a security control it isn't: it's the ownership handle a future "release my hold" endpoint will need, and it's what ties a hold to the confirm that eventually consumes it. An anonymous, unauthenticated flow still benefits from having *some* identity to hang session-scoped features off of - that's what this token is for, not abuse prevention.

### Account lockout creates a symmetric, accepted abuse surface

Arming Identity's account lockout (`lockoutOnFailure: true` in `SignInHandler`) closes the credential-stuffing gap where failed password attempts never counted toward anything. It also means five bad guesses against a *known* email now locks that real account out for 15 minutes, repeatably, forever, for the cost of one HTTP request every 15 minutes. This is the standard tradeoff and it's the one being made deliberately here, not a side effect discovered later: an attacker who already knows (or guesses) a valid email can deny that user access to their own account indefinitely. `SignInHandler` does not distinguish a locked-out account from a wrong password in its response (see below) specifically so that lockout itself can't be used to *discover* which emails are registered - but denial-of-service against a known email is accepted, not mitigated.

### Why `SignInHandler` doesn't have a distinct "account locked" response

A tempting improvement - telling a locked-out user why they're locked out instead of a generic "invalid credentials" - was deliberately rejected. A distinguishable lockout response is only ever reachable for an account that *exists* (an unregistered email can never be locked out), which makes it an enumeration oracle no matter how well request timing is equalized between the "no such user" and "wrong password" branches. `SignInHandler`'s dummy-password-hash-verification (paying the same cost for a nonexistent email as a real wrong-password attempt) would be silently undone by adding a distinguishable status one layer up. The cost is real - a legitimately locked-out user sees the same generic message as a wrong password for the full 15-minute window - and it's accepted in favor of not reopening the enumeration channel the rest of `SignInHandler` was built to close.

## Alternatives considered

- **Require authentication for holds.** Rejected outright: the endpoint's own purpose is pre-checkout availability-checking for guests who haven't signed in yet (and may never - guest checkout is a first-class path through this app). Forcing sign-in here would break the actual product requirement, not just harden it.
- **A CAPTCHA or proof-of-work challenge on the hold endpoint.** Would meaningfully raise the cost of the "zero out inventory" attack. Not adopted in this pass - it's a larger UX and infrastructure commitment than the stay-length/lead-time/rate-limit combination above, which closes the same hole with tools this codebase already has.
- **Make the hold-session cookie cryptographically bind to the request (e.g. a signed token tying the session to an IP)**, so it couldn't be trivially regenerated. Rejected for now: it would still only raise the cost of the attack the rate limiter already bounds by a different mechanism, for real added complexity (signing, key rotation) - revisit only if the rate limiter alone proves insufficient in practice.

## Consequences

- Any new anonymous endpoint should be added to the inventory table above at the time it's created, with an explicit answer to "what can an unauthenticated caller do here, and how many times per minute" - not left to be discovered later the way both the hold endpoint and the confirm endpoint were.
- The account-lockout DoS tradeoff should be revisited if this app ever needs a self-service "my account got locked by someone else" recovery path; none exists today beyond waiting out the 15-minute window.
