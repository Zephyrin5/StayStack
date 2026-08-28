# 0005 - Host is a capability on an account, not a separate account type

**Status:** Accepted

## Context

Every real-world booking platform this project takes inspiration from (Airbnb chief among them) has two roles that overlap: most hosts are also guests, and every host started as an ordinary account before ever listing a property. The signup flow needs to decide whether "host" is a distinct kind of account (its own registration path, its own credentials) or something an existing account can acquire later.

## Decision

One `ApplicationUser`, with a nullable `HostId`. Every self-registered account starts as a plain Customer (`SignUpHandler` assigns the `Customer` role unconditionally). Becoming a host is a separate, later action (`BecomeHostHandler`) that links an existing account to a new `Host` record and adds the `Host` role - not a different signup flow, not a different account type. A user can hold both roles at once, which is the common case, not an edge case.

## Alternatives considered

- **A distinct `Host` account/entity type, separate from `ApplicationUser`.** Rejected: it would force a choice at registration time that real platforms don't force, and would need its own duplicate authentication/credential machinery for something that's fundamentally the same login acting with an additional capability.
- **A `HostId` resolved via a separate lookup/join table instead of a direct nullable FK on the user.** Would work, but adds a join for what's actually a 1:1, rarely-changing relationship (an account either has become a host once, or hasn't) - the direct nullable link is simpler and matches how `host_id` is carried as a JWT claim once present.

## Consequences

- `ICurrentUserProvider`/JWT claims carry `host_id` only once `BecomeHost` has run - any handler needing host context (`IHostAuthorization.RequireHostId()`) must handle the "not a host yet" case, not assume it.
- `AuthorizationPolicies.Host`/`HostOrAdministrator` exist because a user's role set can genuinely contain both `Customer` and `Host` at once, not because they're mutually exclusive tiers.
- Any future host-only feature should assume it's being used by someone who is *also* a customer elsewhere in the app, not a separate persona.
- The "an account either has become a host once, or hasn't" framing above (Alternatives considered) is stated as a property of the relationship, but it's actually a constraint the JWT-claim design now depends on: `host_id` is baked into the access token at sign-in and trusted for the token's full lifetime (`HostAuthorization.RequireHostId()` is a claim read, never a DB check that the `Host` row still exists or is active). That's safe today because host status genuinely is one-way - there's no revocation/deactivation path. If one is ever added, `HostAuthorization` needs a re-validation step (or a shorter-lived claim), or a revoked host stays effectively active until their access token naturally expires.
