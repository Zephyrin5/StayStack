# 0007 - Separate Mediator requests for public vs. owner-scoped near-identical queries

**Status:** Accepted

## Context

Several list endpoints come in pairs that differ only by which rows are visible: `GetProperties` (public browse, anonymous) vs. `GetMyProperties` (a host's own listings, authenticated); `GetMyBookings` (a customer's own bookings) vs. `GetHostBookings` (bookings against a host's own properties). In each pair, the underlying query differs from its counterpart only by which filter gets applied, and the response shape is identical or nearly so. It would be easy - and looks like good DRY practice on the surface - to share one Mediator request type between the public and owner-scoped versions, with a `HostId`/similar field that's either client-supplied (public) or derived from the caller's token (owner-scoped).

## Decision

Keep them as separate `IRequest` types with separate handlers, even where the query logic is nearly identical. Share only the parts that are genuinely identical - query-building and response mapping - as a plain method call (`PropertySummaryMapper`), not through the Mediator dispatch layer.

## Alternatives considered

- **One shared Mediator request, with the "owner" endpoint populating a filter field the "public" endpoint would otherwise leave for the caller to supply.** Rejected for two concrete reasons, not just "feels cleaner to separate":
  1. `TelemetryPipelineBehavior` keys every span/metric/log line off `typeof(TMessage).Name` alone. A shared request type would make an anonymous browse call and an authenticated owner's own-listings call indistinguishable in traces and metrics - different caller population, different traffic shape, different alerting needs, collapsed into one bucket with no way to split them back apart later without a breaking change.
  2. The shared field would need two different trust levels depending on which endpoint populated it - derived-from-claim (trusted) for the owner endpoint, vs. client-supplied (untrusted) for the public one. That's exactly the kind of implicit coupling that turns into a real authorization bug once either endpoint evolves independently of the other and someone forgets which trust level applies where.

## Consequences

- A small amount of duplication is accepted at the request/handler level in exchange for each endpoint having its own telemetry identity and its own unambiguous trust boundary.
- Any future "public version + owner-scoped version" pair of an endpoint should default to this same split, not to sharing a request type "to avoid duplication" - the duplication being avoided is smaller than the coupling it would introduce.
