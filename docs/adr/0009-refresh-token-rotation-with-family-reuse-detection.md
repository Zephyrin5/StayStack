# 0009 - Refresh-token rotation with family-based reuse detection

**Status:** Accepted

## Context

Refresh tokens need to balance session longevity against the damage a stolen token can do. Three related problems:

1. Two concurrent requests presenting the same still-valid refresh token could both pass a naive "is this valid" check before either commits its rotation, both succeeding and effectively forking a session in two.
2. A stolen, already-used refresh token replayed later is a strong signal of theft, and should do more than fail that one request.
3. Those two answers collide. Once consumption is atomic, the loser of a concurrent rotation presents a token that is revoked and replaced - which is exactly what a replayed stolen token looks like. Two tabs sharing a refresh cookie, or one client answering two parallel 401s, is ordinary client behaviour, and treating it as theft revokes the replacement the winner has just been handed. The user is signed out by the mechanism meant to protect them.

## Decision

- **Rotation, not reuse.** Every successful refresh consumes the presented token and issues a new one (`AuthTokenProvider.GenerateRefreshToken`/`ValidateRefreshToken`). `RefreshToken.ParentTokenId`/`ReplacedByTokenId` form an explicit chain, so a family's history can be traced after the fact.
- **Atomic consumption.** Validating and revoking a token is a single conditional `UPDATE ... WHERE TokenHash = @hash AND !IsRevoked AND ExpiresAt > now()`, via `ExecuteUpdateAsync`, not a SELECT-then-check-then-UPDATE. Two concurrent callers presenting the same token can no longer both observe "not yet revoked" and both rotate it - only one `UPDATE` can match before the other commits; the loser is correctly classified as reuse rather than silently succeeding a second time.
- **The loser of a concurrent rotation is benign, within a grace.** A presented token that is revoked, carries a `ReplacedByTokenId`, and was revoked less than `AuthTokenConfiguration.RotationReuseGraceSeconds` ago (30) is classified as the other half of a rotation already in flight. The request is refused with the same 401 every other rejection gives, and the family is left alone.
  - `ReplacedByTokenId` is what separates the two ways a token becomes revoked: rotation sets it, sign-out does not. A token presented after sign-out was never superseded, so it still reaches reuse detection.
  - **The loser is not handed the winner's child token.** It would spare the client a round trip, and it would also hand a live credential to whoever presented a token twice - which is the thief as readily as the tab. A 401 is recoverable: the real client re-reads its cookie, which the winner has already updated.
  - Seconds, not minutes. The window is sized for requests that were already in flight together, and every second of it is a second in which a replay is refused without being detected.
- **Family-scoped reuse detection.** Every token descended from one sign-in shares a `FamilyId`. Presenting an already-revoked token doesn't just fail - `RevokeFamilyAsync` revokes every token in that family, invalidating that entire session lineage. A fresh sign-in starts a new family; rotation carries the existing one forward.
- **Scoped to the family, not the whole account.** Reuse detection revokes only the replayed token's own family, not every session the user has anywhere. A stolen token on one device shouldn't sign the user out of an unrelated device's session too.
- Cleanup deletes on `ExpiresAt`, never on `IsRevoked` - a revoked-but-not-yet-expired token still has to exist for a later replay of it to be correctly classified as reuse (family revocation) rather than "doesn't exist" (silently ignored). Only a token past its own expiry has no further reuse-detection value.

## Alternatives considered

- **Long-lived, non-rotating refresh tokens.** Simpler, but a stolen token remains valid for its entire lifetime with no way to detect the theft short of the legitimate user also trying to use it and failing.
- **Rotation without family tracking - just revoke the single reused token.** Detects reuse but doesn't respond to it meaningfully: an attacker who already replayed a stolen token has likely also rotated it forward at least once, and revoking only the one presented token leaves their now-current token (further down the same chain) still valid.
- **Revoke every session the user has, account-wide, on any detected reuse.** More aggressive, but conflates "one device's token was stolen" with "this user's entire account is compromised," signing out unrelated legitimate sessions on unrelated devices for a problem localized to one.

## Consequences

- A stolen-and-replayed token costs the attacker (and the legitimate user, once they next try to refresh) that one session lineage, not the whole account - a deliberate scope tradeoff, not an oversight.
- **A stolen token replayed within the grace of a legitimate rotation is refused, and not detected.** That is the price of the grace, stated plainly: for up to 30 seconds after a real refresh, a replay of the token it consumed looks like the tab that lost. The attacker gains nothing from that request - no token is issued - but the family is not revoked, so the theft goes unrecorded. Outside the window, and for every token not consumed by a rotation, detection is unchanged. Shortening the grace narrows that window and widens the one where an ordinary double refresh signs a user out; 30 seconds is well past any in-flight pair and well inside a human-scale replay.
- Any future change to refresh-token handling needs to preserve the atomic-`ExecuteUpdateAsync` property - reintroducing a SELECT-then-UPDATE window would reopen the exact race this design closes.
- **`RefreshTokenGraceTests` holds the classification** from both sides: a burst of ten leaves the winner's token usable, a rotation backdated inside the grace is refused with the family intact, one backdated past it revokes the family, and a token revoked by sign-out still does. Removing the grace fails the first two and leaves the last two passing, which is how the split was checked. `RefreshTokenConcurrencyTests` still counts the statuses; it passed throughout the bug, which is why the burst's follow-up refresh is asserted rather than the count alone.
