# 0023 - The management token is exchanged once, not carried everywhere

**Status:** Accepted

Narrows the bearer-credential handling in [ADR-0016](0016-trust-model-for-anonymous-endpoints.md) and adds a second token type alongside [ADR-0009](0009-refresh-token-rotation-with-family-reuse-detection.md)'s. Amends [ADR-0004](0004-module-boundaries-via-contracts-projects.md) with one more Api-implemented interface.

## Context

`BookingManagementToken` is what makes guest checkout work without an account. It is a bearer credential: whoever holds it can view, cancel and review a booking. Its lifetime is measured in months - `CheckOut + ManagementTokenLifetimeDaysAfterCheckOut`, 90 days by default - because the review window depends on it.

It was presented on **every** management call: four requests across three modules (`GetBookingForManagement`, `CancelBooking`, `CreateStayReview`, `InitiateTransaction`). One of those is a `GET`, so the credential travelled in `?managementToken=` - reaching the access log of every hop, the browser's history, and the `Referer` header of any outbound link on the page.

The credential was not weak. It was simply *everywhere*, repeatedly, for months.

## Decision

**Exchange the token for a short-lived, booking-scoped session, once.**

```
POST /api/bookings/{bookingId}/manage/session
{ "managementToken": "..." }        <- body, the only request that carries it

-> { "sessionToken": "...", "expiresAt": "..." }
```

Subsequent management calls send `Authorization: Bearer <sessionToken>`. The management token stays valid and is **not** rotated or consumed, so the guest's link works next week and next month; re-exchanging costs them nothing. The short session is what limits exposure, not single use.

### A signed claim, not a row

The session is a JWT: no new table, no new sweep job, expiry enforced by the authentication handler rather than by handler code that could forget. The trade is no revocation inside the session window, which is the same trade the access token already makes and is acceptable at 45 minutes.

The long-lived token behind it stays revocable in the only way it ever was - by deleting its row.

### The boundary is the audience, not a claim

Both tokens are signed with the same key by the same issuer. If the separation between "user identity" and "booking session" rested on a `scope` claim, it would hold only where somebody remembered to check it - and by then the default bearer scheme would already have validated the signature and built an authenticated principal.

So the session carries a different **audience**, and a second `JwtBearer` scheme validates it. A booking session presented to an ordinary endpoint fails `ValidAudience` on the default scheme and never becomes a principal at all. `BookingSessionAudience` is *derived* from `Audience` rather than configured separately, so the two cannot be set equal by accident.

Two more limits follow from the same reasoning:

- **A session cannot mint a session.** `CreateBookingSessionHandler` passes `sessionBookingId: null`, so renewal requires the original token. Otherwise a 45-minute credential renews itself indefinitely and the lifetime stops meaning anything.
- **A session for booking A cannot act on booking B.** The claim proves the bearer once held A's token, which says nothing about the booking a request names. `BookingAccessChecker` compares the two.

### Declared in Bookings, implemented in the Api layer

`IBookingSessions` follows `BuildingBlocks.Identity.ICurrentUserProvider`: declared where it is needed, implemented where the capability lives. Bookings does not reference Identity - **nothing does except the Api project** - and the alternative was a new module edge for something no part of Bookings needs to know. Identity gained a deliberately generic `GenerateScopedToken(audience, claims, lifetime)` rather than a booking-shaped method, so JWT signing stays in exactly one place without Identity learning what a booking is.

### Cookie mode stays same-origin only

`CookieSecurityOptions.SameSite` defaults to `Lax` while the CORS policy calls `AllowCredentials()`. Those are consistent for a same-site deployment and silently contradictory for a cross-site one: CORS allows the origin, the preflight passes, and the browser never attaches the cookie - cookie-mode refresh 401s with nothing wrong in any log.

**Bearer is the primary carrier; cookie mode is a same-origin convenience.** A cross-origin SPA uses body tokens, which already works and avoids owning CSRF protection on every cookie-authenticated endpoint. A new startup check makes the contradiction loud instead of silent.

## Consequences

- **The startup check needs an origin the app cannot discover.** Behind a TLS-terminating proxy the bound addresses are the proxy's, so anything derived at runtime describes the wrong side of the hop - the same mistake that once shipped refresh tokens without `Secure`. `Cookies:ApiOrigin` is therefore declared. It is optional, and when absent the check logs that it *could not run* rather than staying silent, because silence reads as "checked, fine".
- **Registrable-domain comparison is approximate and biased toward not failing.** Last-two-labels gets `a.co.uk` vs `b.co.uk` wrong, calling them same-site. That is the right way to be wrong for a startup hint: a missed warning leaves a deployment where it already was, a false positive refuses to start a correct one. A Public Suffix List dependency is not worth it here.
- **The old carrier still works.** All four endpoints still accept `ManagementToken`, so nothing breaks while clients migrate. Removing it - the request properties, the `BookingAccessChecker` branch, and the endpoint summaries that document `?managementToken=` - is a follow-up, gated on clients actually being on the exchange.
- **`[Sensitive]` was missing on every `ManagementToken` property**, and on `ConfirmBookingRequest`'s guest name, email, phone and promo code, and on the management token in `ConfirmBookingResponse`. The redaction model is a denylist, so each gap is paid for on the day telemetry is switched on - and `ConfigureObservabilityServices` is currently commented out, which is exactly what made this free to fix now and expensive to discover later.
- **New request/response types must be registered in the module's `JsonSerializerContext`.** Native AOT source generation ([ADR-0001](0001-native-aot-compatibility.md)) means an unregistered type fails at runtime with a 500, not at compile time - the exchange endpoint did exactly that on first run.
- **Link delivery is not built.** There is no email infrastructure; the management link is rendered in the checkout receipt. The fragment-based hand-off (`/bookings/manage/{id}#t=<token>`, read once, exchanged, then `history.replaceState`) is implemented on the client because it is worth having whether the link arrives by email or by copy-paste - a fragment is never sent to any server, so it appears in no access log and no `Referer`. Mobile deep links are moot until there is an app.

## Still open

- **Cancel and view carry equal weight.** Anyone who sees the link can cancel a stay. Requiring the booking's guest email to confirm a cancellation turns a leaked link from "cancel someone's holiday" into "read their itinerary" - one form field, and a larger real-world improvement than any transport change here.
- **There is no way to revoke a management token.** One row per booking, no expiry column, no revocation. With a link UX the token lives in inboxes indefinitely, and the ability to invalidate one matters more than it did when the token was only ever held by an active session.
