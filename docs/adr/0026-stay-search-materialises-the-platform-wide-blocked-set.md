# 0026 - Stay search materialises a platform-wide blocked set, and that is a known ceiling

**Status:** Accepted - recording a boundary, not endorsing it

Records the cost of `IUnitAvailabilityLookup.GetBlockedUnitIdsAsync`, the cross-module read ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)) that lets Catalog filter search results by availability held in Bookings ([ADR-0021](0021-availability-is-part-of-bookings.md)).

## Context

`GetPropertiesHandler` answers "which properties have a unit free for these dates". Availability lives in Bookings, which sits downstream of Catalog, so Catalog cannot join against `unit_availability_holds`. It asks instead, through `IUnitAvailabilityLookup.GetBlockedUnitIdsAsync`, and filters its own page against the answer in memory.

The answer is **every unit id, platform-wide, with an active hold or booking overlapping the requested window.** Not every unit in the candidate page - every unit on the platform.

This was documented as bounded, and the wording is the reason it deserves an ADR rather than a one-line fix:

> the result is exactly as large as this window's actual bookings/holds, not the whole unit table, and `MaxStayNights` bounds the window's width so this can't grow past "every unit ever booked" on a wide-open search.

Every clause of that is true, and the conclusion does not follow. `MaxStayNights` bounds **time**, not **cardinality**. A one-night search is the *narrowest* window the system allows, and on a platform with a million occupied units that night it returns a million ids - allocated, deserialised, and put in a `HashSet` in this process, to filter a page of twenty results.

The comment was worse than no comment. Someone reading it and asking "is this bounded?" got a confident yes, with a plausible-looking mechanism attached. The failure mode is that nobody looks again until a production search times out.

## Decision

**This is recorded as a known scale ceiling, not fixed here, and not described as bounded anywhere.**

Two honest facts, in place of the false one:

- The set scales with **platform occupancy during the window**, which grows with the business.
- `MaxStayNights` bounds how *wide* a window may be, which limits one multiplier and no others. How far out the window sits has no bearing at all.

It is genuinely fine at current scale - a development platform with tens of units - which is why this is a recorded boundary rather than an outage. What makes it worth an ADR is that nothing about the code gets *slower gradually* in a way anyone notices: the read stays instant until the day the set no longer fits comfortably in memory, and then it is a rewrite under pressure.

### What the fix is, when it is time

A denormalised availability read model - a projection Catalog can query directly, so the filter happens in the database against the candidate page rather than in this process against the platform.

Explicitly **not** these:

- **A smaller array / paging the blocked set.** It is the wrong axis. The problem is that the query is unrelated to the page being filtered, so any bound on the transfer just moves the same work.
- **Passing the candidate unit ids in to narrow the lookup.** Trades one unbounded list for another, and Catalog would have to materialise its own candidate set first - which is the thing the current shape was designed to avoid.
- **Letting Catalog join `unit_availability_holds` directly.** That is the boundary [ADR-0004](0004-module-boundaries-via-contracts-projects.md) exists to hold and the Availability merge spent a phase reinforcing. Performance is a reason to build a read model, not a reason to delete a module boundary.

## Consequences

- **The interface's own doc comment says all of this**, because that is where someone will be standing when it matters. An ADR nobody is pointed at from the code is a document, not a decision.
- **The trigger is occupancy, not unit count.** A platform with many units and few bookings is unaffected. The metric to watch is distinct units held or booked on a single night.
- **This is the second comment in this codebase found overstating a bound**, the first being `ExpireUnpaidBookingsJob`'s claim that its re-check under the row lock made a payment race impossible (see [ADR-0025](0025-retried-work-is-built-inside-the-retry.md) and the payment/expiry fix that followed). Both read as careful, both named a real mechanism, and both attached it to a conclusion it did not support. A stated bound should name the quantity it bounds - "width, not cardinality" is the distinction that was missing here, and "Bookings-side state, not payment state" was the one missing there.
