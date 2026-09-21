# 0030 - Access tokens are checked against the account on every request

**Status:** Accepted

Owns what an access token is trusted to say. Rotation, families and reuse detection are [ADR-0009](0009-refresh-token-rotation-with-family-reuse-detection.md); the cache this uses is bounded by [ADR-0024](0024-every-memory-cache-entry-declares-its-size.md).

## Context

A JWT is a snapshot. `AuthTokenProvider` writes the account's roles and its `host_id` into the token at sign-in, and until now nothing read the account again: signature, issuer, audience and expiry were the whole check. Every capability in the token therefore survived for the token's full lifetime, whatever happened to the account behind it.

That is a window, and its width is a configuration value. Taking the Administrator role away from a compromised account left that account administering the system until the token expired. Removing a host's `PropertyStaff` role left them writing to the property. Nothing an operator could do shortened it: revoking the refresh token stops the *next* token being issued, and the current one keeps working.

The same snapshot is a usability problem in the other direction. `BecomeHost` writes the host link and issues a replacement token carrying it, but any other token the account already holds - a second tab, a phone - still says the caller is not a host, and the host endpoints stay shut for it until it expires.

The account already carries the value this needs. `ApplicationUser` inherits `SecurityStamp` from `IdentityUser<Guid>`: a random value ASP.NET Core Identity moves whenever a credential-affecting change is made, and which `UserManager.UpdateSecurityStampAsync` moves on demand.

## Decision

**A token is accepted only while the stamp it was minted with is still the account's.**

- `AuthTokenProvider.GenerateJwtToken` writes the account's stamp into the token as `security_stamp`. Every token this application issues goes through it, so every token carries one.
- `SecurityStamps.ValidateAsync` runs on `JwtBearerEvents.OnTokenValidated` for the user scheme, after the signature and lifetime checks that decide the token was ours. It reads the account's current stamp and fails the request when the two differ. A token without the claim, or without a readable `sub`, is refused rather than skipped.
- The read goes through `HybridCache` under `SecurityStamps.CacheWindow`, 30 seconds. The library writes the entry's serialized length as its `Size`, which is what [ADR-0024](0024-every-memory-cache-entry-declares-its-size.md) requires of anything entering the shared in-process budget.
- **`AssignRoleHandler`, `RemoveRoleHandler` and `BecomeHostHandler` move the stamp and then evict the cached copy**, in that order and after their write has committed. Moving it is what retires the outstanding tokens; evicting is what makes that happen now rather than at the end of the window.

The booking-session scheme is untouched. It authenticates a booking, not an account, and has no account to check against - which is the same reason it already validates a different audience ([ADR-0023](0023-booking-management-sessions.md)).

Signing out does not move the stamp, deliberately: it ends one session, and moving the stamp would sign the account out everywhere it is signed in.

## Alternatives considered

- **A shorter access-token lifetime.** Rejected as the answer: it narrows the window for every request in the system rather than closing it for the one account that changed, and it buys that with a refresh round trip per client per few minutes. It also cannot reach zero - whatever the lifetime, a removed role is still honoured for it.
- **A revocation list of token ids.** Rejected: it needs storage proportional to revocations rather than to accounts, it has to be consulted on every request anyway, and it answers "was this token revoked" when the question is "is this account still like this". The stamp answers the second and costs one value per account.
- **Reading the account on every request, uncached.** Rejected: one round trip per authenticated request, on the hot path of every endpoint, to detect a change that almost never happened. The cache makes the common answer free and bounds the staleness at a number this ADR states.
- **Invalidating by TTL alone, without the explicit eviction.** Rejected, and this is the case the tests pin: the account's own read populates the cache, so a role removed a moment later would keep being honoured for the rest of the window even on the instance that removed it.

## Consequences

- **One cache read per authenticated request, and a database read per account per 30 seconds.** The read is a single-column lookup by primary key. The cache entry is a stamp, tens of bytes, but it competes for the same 64 MB budget as the Catalog read paths ([ADR-0024](0024-every-memory-cache-entry-declares-its-size.md)).
- **The window is 30 seconds, not the token lifetime.** That is the trade this makes: a capability taken away is honoured for up to `SecurityStamps.CacheWindow` on a process that did not handle the change, instead of up to `AccessTokenLifespanInMinutes` everywhere.
- **On the instance that handled the change, the window is zero.** The eviction is local, so with more than one instance the others fall back to the TTL. Closing that too would need the cached stamp to be shared or a backplane to carry the eviction - the same decision `docs/scale-out-findings.md` records for the rest of the cache, and not one this ADR makes.
- **An eviction lost between the commit and the cache is bounded, not silent.** The TTL is the backstop: a crash after the commit leaves the old stamp cached for the rest of the window and no longer.
- **A changed account invalidates its own caller's token too.** An administrator who grants themselves a role gets a 401 on their next request and refreshes. `BecomeHostHandler` avoids it by returning the replacement token in its response; the role endpoints do not, because the account they change is not the caller.
- **Every token in flight at deployment stops working.** They carry no `security_stamp` and are refused. There is no data to migrate, so nothing accommodates them.
- **Anything else that moves the stamp inherits the window.** Identity moves it on a password change of its own accord, and this application has no such endpoint today. One added later gets the 30-second bound for free and immediacy only by evicting after its commit, the way the three handlers here do. Nothing enforces that pairing: the two steps are deliberately separate because `BecomeHostHandler` needs the move inside its transaction and the eviction after it, and a helper doing both could not serve that case.
- **`SecurityStampTests` proves the boundary**, including the negative: with the check unwired all four cases pass a token that should be refused, and with the eviction removed the role-removal case fails on its own.
