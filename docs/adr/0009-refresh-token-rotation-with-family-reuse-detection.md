# 0009 - Refresh-token rotation with family-based reuse detection

**Status:** Accepted

## Context

Refresh tokens need to balance session longevity against the damage a stolen token can do. Two related problems: (1) two concurrent requests presenting the same still-valid refresh token could both pass a naive "is this valid" check before either commits its rotation, both succeeding and effectively forking a session in two; (2) a stolen, already-used refresh token being replayed later is a strong signal of theft, and should do more than just fail that one request.

## Decision

- **Rotation, not reuse.** Every successful refresh consumes the presented token and issues a new one (`AuthTokenProvider.GenerateRefreshToken`/`ValidateRefreshToken`). `RefreshToken.ParentTokenId`/`ReplacedByTokenId` form an explicit chain, so a family's history can be traced after the fact.
- **Atomic consumption.** Validating and revoking a token is a single conditional `UPDATE ... WHERE TokenHash = @hash AND !IsRevoked AND ExpiresAt > now()`, via `ExecuteUpdateAsync`, not a SELECT-then-check-then-UPDATE. Two concurrent callers presenting the same token can no longer both observe "not yet revoked" and both rotate it - only one `UPDATE` can match before the other commits; the loser is correctly classified as reuse rather than silently succeeding a second time.
- **Family-scoped reuse detection.** Every token descended from one sign-in shares a `FamilyId`. Presenting an already-revoked token doesn't just fail - `RevokeFamilyAsync` revokes every token in that family, invalidating that entire session lineage. A fresh sign-in starts a new family; rotation carries the existing one forward.
- **Scoped to the family, not the whole account.** Reuse detection revokes only the replayed token's own family, not every session the user has anywhere. A stolen token on one device shouldn't sign the user out of an unrelated device's session too.
- Cleanup deletes on `ExpiresAt`, never on `IsRevoked` - a revoked-but-not-yet-expired token still has to exist for a later replay of it to be correctly classified as reuse (family revocation) rather than "doesn't exist" (silently ignored). Only a token past its own expiry has no further reuse-detection value.

## Alternatives considered

- **Long-lived, non-rotating refresh tokens.** Simpler, but a stolen token remains valid for its entire lifetime with no way to detect the theft short of the legitimate user also trying to use it and failing.
- **Rotation without family tracking - just revoke the single reused token.** Detects reuse but doesn't respond to it meaningfully: an attacker who already replayed a stolen token has likely also rotated it forward at least once, and revoking only the one presented token leaves their now-current token (further down the same chain) still valid.
- **Revoke every session the user has, account-wide, on any detected reuse.** More aggressive, but conflates "one device's token was stolen" with "this user's entire account is compromised," signing out unrelated legitimate sessions on unrelated devices for a problem localized to one.

## Consequences

- A stolen-and-replayed token costs the attacker (and the legitimate user, once they next try to refresh) that one session lineage, not the whole account - a deliberate scope tradeoff, not an oversight.
- Any future change to refresh-token handling needs to preserve the atomic-`ExecuteUpdateAsync` property - reintroducing a SELECT-then-UPDATE window would reopen the exact race this design closes.
