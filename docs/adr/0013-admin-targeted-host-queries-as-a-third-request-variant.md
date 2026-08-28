# 0013 - Admin-targeted host queries as a third request variant

**Status:** Accepted

## Context

Admin user/role management needed a way for an Administrator to browse into a specific host's
portal - their properties and bookings - the way that host would see them. Every host-management
*write* endpoint (`CreateUnitHandler`, `UpdatePropertyHandler`, all four pricing-rule handlers, etc.)
already lets an Administrator act on any host's resources, via the same
`if (!Roles.Contains(AuthorizationPolicies.Administrator)) RequireOwnership(...)` bypass on every one of them - so no new
write surface was needed. But the *read* side had no equivalent: `GetMyPropertiesHandler` and
`GetHostBookingsHandler` both resolve `hostId` exclusively from the caller's own token via
`IHostAuthorization.RequireHostId()`, with no parameter to target a different host at all.

[ADR-0007](0007-separate-requests-for-public-vs-owner-scoped-queries.md) already established the
pattern for two access levels on the same underlying query - public (anonymous, filter is
client-supplied) and owner-scoped (authenticated, filter is derived from the caller's own token) -
as separate Mediator request/handler pairs rather than one shared request with a dual-trust field.
Admin-targeted access is a third, structurally different level: authenticated, `Administrator`-only,
and the filter (`HostId`) *is* client-supplied - but unlike the public case, it isn't just a browse
filter, it's an id the handler has to actively verify refers to something real before trusting it.

## Decision

Admin-targeted host queries (`GetHostPropertiesRequest`, `GetBookingsForHostRequest`) are a third,
explicit request/handler variant, following ADR-0007's same reasoning: `HostId` comes from the route,
the endpoint is `Policies(AuthorizationPolicies.Administrator)`-gated, and the handler validates the
id via the module's own `I*Lookup` contract (`IHostLookup.ExistsAsync`) before querying - the same
existence check `AdminCreatePropertyHandler` already uses for the one existing precedent of an
admin-targeted *write*. No new response shapes: both reuse the exact same `PropertySummary`/
`HostBookingSummary` DTOs their self-scoped counterparts already return - a host's own view and an
admin's view of the same rows have no reason to look different.

## Alternatives considered

- **Add an optional `HostId` to `GetMyPropertiesRequest`/`GetHostBookingsRequest`, with an
  admin-only branch inside the same handler when it's set.** Rejected for the identical reasons
  ADR-0007 already gives for not merging public and owner-scoped requests: it collapses two different
  trust boundaries (derived-from-token vs. client-supplied-and-verified) into one handler, makes the
  two access patterns indistinguishable in `TelemetryPipelineBehavior`'s per-type traces, and creates
  exactly the kind of implicit coupling ADR-0007 was written to avoid - a future change to the
  self-scoped path could silently affect the admin path sharing its handler, or vice versa.
- **A generic "act as user X" mechanism** (e.g. a header or claim letting an Administrator's calls be
  reinterpreted as another user's) instead of per-feature admin-targeted requests. Rejected as much
  larger, more implicit surface for a need that's fully met by two new read endpoints - every write
  path already works via the existing ownership-bypass pattern, so there was never a need for the
  *caller's own identity* to change, only for two specific reads to accept an explicit target.

## Consequences

- Two more request/handler pairs than a shared-request design would need - accepted for the same
  trade-off ADR-0007 already made.
- Any future "let an admin view another host/customer's data" need should default to this same shape:
  a new request taking the target id from the route, `Administrator`-only policy, existence validated
  via the owning module's lookup contract - not a parameter bolted onto the self-scoped request.
- **Correction:** the `TelemetryPipelineBehavior` per-type-traces argument above (Alternatives considered)
  is currently moot, not just theoretical - `ConfigureObservabilityServices`, the call that registers
  `TelemetryPipelineBehavior` at all, is commented out in `Program.cs` ("Disabled until Grafana is
  configured"). Nothing is currently collecting per-request-type telemetry. The trust-boundary argument
  (derived-from-token vs. client-supplied-and-verified) is what's actually carrying this decision today
  and is sufficient on its own - see the identical correction on [ADR-0007](0007-separate-requests-for-public-vs-owner-scoped-queries.md).
