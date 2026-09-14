# 0010 - Postgres exclusion constraint for double-booking prevention

**Status:** Accepted. Table owned by Bookings per [ADR-0021](0021-availability-is-part-of-bookings.md); hold lifecycle per [ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md).

## Context

Two overlapping holds or bookings for one unit is the one invariant this application cannot get wrong. A check-then-insert in application code has a window in which a second request passes the same check before either commits, and a lock alone holds only for the writers that remember to take it.

## Decision

**The exclusion constraint enforces non-overlap.** `unit_availability_holds_overlap_excl` is `EXCLUDE USING gist (unit_id WITH =, stay_range WITH &&)` on `unit_availability_holds`, with no status predicate. It is hand-written SQL in a migration because EF Core has no fluent API for exclusion constraints ([npgsql/efcore.pg#1975](https://github.com/npgsql/efcore.pg/issues/1975)); its name is `UnitAvailabilityHoldConfiguration.OverlapExclusionConstraint`. `HoldAvailabilityHandler` does not check for overlap itself: it inserts, and translates a violation of that constraint (matched by name) into `UnitUnavailableException`.

**`UnitAvailabilityLock` coordinates the writers the constraint cannot.** Taking a hold and archiving a unit both take the unit's advisory lock exclusively, inside their transactions and before the checks it protects (`UnitAvailabilityLock.AcquireForHoldSql`, `AcquireForArchivalSql`; archival takes it in `UnitArchival.EnsureArchivableAsync`, per unit, after `PropertyUnitsLock` when a whole property is archived). Lock order and the other advisory locks are [ADR-0028](0028-advisory-locks-and-lock-order.md). The lock serves two purposes the constraint cannot:

- **Archival.** Archiving writes `units` in Catalog, a different table and module. The constraint cannot see it, so without the lock an archive's "no active holds" check and a hold insert can interleave, leaving an archived unit with live inventory. A row lock on `units` would do the same job but require Bookings to name a Catalog table ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)); an advisory key needs no shared schema.
- **Concurrent holds on one unit.** A GiST exclusion constraint is checked after each inserter writes its own index entry, so concurrent overlapping inserters wait on each other's uncommitted entries and deadlock until `deadlock_timeout` breaks the cycle. Every loser becomes a deadlock victim and is retried with backoff instead of being rejected. Serialised per unit, each insert checks committed rows and a loser is rejected at once. Holds for different units never share a key.

The lock is a performance and coordination mechanism, not the guarantee. A writer that skips it still cannot commit an overlap; it can only deadlock.

Supporting details:

- **Stale `held` rows block ranges until deleted.** The constraint applies to every row regardless of status or expiry. `HoldAvailabilityHandler` deletes the unit's expired `held` rows under the lock before inserting, and `ExpiredHoldsSweepJob` ([ADR-0002](0002-tickerq-for-background-jobs.md)) deletes ranges nobody retries. `GetPriceCalendarHandler` treats an expired `held` row as available for display without deleting it.
- **The explicit transaction runs inside the execution strategy.** A manually started transaction is not retried per operation, so the handler wraps it in `CreateExecutionStrategy().ExecuteAsync(...)`, and the retry policy includes `40P01` and `40001`. The transaction is Serializable for the per-client hold cap, a count-then-insert across units that the per-unit lock does not cover.

## Alternatives considered

- **Check-for-overlap-then-insert in application code.** Rejected: that is the race the constraint closes.
- **A per-unit lock instead of the constraint.** Rejected as the guarantee: every write path would have to remember the lock forever, and one that forgot could double-book. The lock is used alongside the constraint for coordination and throughput, never in place of it.
- **The constraint alone.** Rejected: it cannot exclude archival, and under contention it arbitrates by deadlock - ten concurrent hold requests for one unit took over a minute.
- **Application-level distributed lock (Redis, etc.).** Rejected: new infrastructure for a guarantee Postgres provides, with the lock provider as an added failure mode.

## Consequences

- The guarantee lives in raw migration SQL, which a migration squash regenerated from the model would lose. `SchemaInvariantsTests` asserts the constraint exists in the live schema.
- `HoldExclusionConstraintTests` drives concurrent inserts at the database directly: through the handler's lock protocol it requires one winner and clean rejections for every loser, and with no lock it requires that two overlapping holds never both commit. `HoldAvailabilityConcurrencyTests` covers the same invariant through the handler.
- The constraint bounds overlap, not how much inventory a caller can hold. Stay-length and lead-time caps and the per-client hold cap bound that; see [ADR-0016](0016-trust-model-for-anonymous-endpoints.md).
- If npgsql/efcore.pg#1975 is implemented, the constraint can move into the model, as [ADR-0011](0011-prefer-model-config-over-migration-sql.md) did for the transactions partial unique index.
