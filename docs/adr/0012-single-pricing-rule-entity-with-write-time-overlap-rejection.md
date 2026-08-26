# 0012 - Single PricingRule entity with write-time overlap rejection

**Status:** Accepted

## Context

Units priced at a flat `BasePrice` needed host-controlled dynamic pricing: date-range overrides
(seasonal/holiday pricing), day-of-week multipliers (weekend pricing), and length-of-stay discounts
(weekly/monthly discounts), with room to add more rule types later. Two existing handlers both need the
resolved price for a unit - `HoldAvailabilityHandler` (the actual charged price, snapshotted onto a
hold) and `GetPriceCalendarHandler` (the public calendar preview, a hot Dapper-backed path behind a 30s
`HybridCache`) - and they must never disagree, since a guest booking after seeing the calendar expects
the calendar's price to be what they're charged.

## Decision

**One discriminated `PricingRule` entity/table**, not one table per rule type. A `RuleType` enum
(`DateRangeOverride`/`DayOfWeekMultiplier`/`LengthOfStayDiscount`) picks which of several nullable typed
columns (`DateRange`/`OverridePrice`, `DaysOfWeek`/`Multiplier`, `MinNights`/`DiscountPercent`) are
populated on a given row. This keeps "give me every rule for this unit" a single indexed query, which is
what both handlers need, and mirrors `Unit`'s own existing convention of ad-hoc typed fields
(`BasePrice`/`Currency`) over introducing a value object or polymorphic hierarchy nothing else in this
module uses.

**Fixed, non-configurable resolution order**, implemented once in a pure `PricingCalculator`
(`src/Modules/Catalog/Domain/PricingCalculator.cs`) that both `HoldAvailabilityHandler` and
`GetPriceCalendarHandler` call, instead of a `Priority` field or duplicating the logic in each consumer
(or in SQL, for the calendar's hot path): for a given night, an active date-range override is the
absolute price; otherwise `BasePrice` times any active day-of-week multiplier matching that weekday, or
just `BasePrice`. A length-of-stay discount, if the stay's total night count meets its threshold, is
applied to the summed subtotal - it's a whole-stay concept, so it's deliberately excluded from the
single-day nightly resolution the calendar preview uses.

**Overlapping rules of the same type are rejected at write time**, in the
`CreatePricingRuleHandler`/`UpdatePricingRuleHandler` (not the FluentValidation validator - consistent
with `CreateUnitRequestValidator`'s existing convention that database-dependent checks belong in the
handler), rather than resolved with a priority/tie-break concept at read time:

- `DateRangeOverride`: reject if the new range overlaps any existing active override for the unit.
- `DayOfWeekMultiplier`: reject if the new day-of-week set shares any day with an existing active rule.
- `LengthOfStayDiscount`: simplified further to **at most one active rule per unit** - avoids ambiguous
  threshold-overlap math (does a 7-night rule "overlap" a 14-night one?) for a product need that doesn't
  yet call for tiered discounts.

No GIST exclusion constraint (unlike `unit_availability_holds`, [ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)).
The overlap check runs as a plain EF LINQ query against that unit's small existing same-type rule set,
staying in fluent config per [ADR-0011](0011-prefer-model-config-over-migration-sql.md)'s default rather
than adding raw migration SQL.

## Alternatives considered

- **One table per rule type.** Rejected - three DbSets/configs, and every rule-loading call site (both
  handlers, the overlap check) would need three queries or a `UNION` instead of one.
- **A `Priority` field to resolve overlapping rules at read time.** Rejected for v1 - adds a whole
  tie-breaking UX and validation surface the actual product need ("simple, predictable host-set rules")
  doesn't require. Can be introduced later as an additive change without migrating existing rows.
- **A Postgres GIST exclusion constraint for date-range overlap, mirroring `unit_availability_holds`.**
  Rejected - that mechanism exists specifically for the double-booking invariant under real concurrent
  write pressure (many guests racing to book the same unit). Pricing-rule authoring is a low-frequency,
  single-host admin action; an in-memory EF check keeps the logic in one readable place instead of
  requiring a raw-SQL migration for a concurrency profile that doesn't exist here.
- **Resolving prices in SQL inside `GetPriceCalendarHandler`'s existing Dapper query**, to avoid the
  extra EF round trip per calendar request. Rejected - it would mean two independent implementations of
  the same precedence logic (one in SQL, one in C# for `HoldAvailabilityHandler`), free to silently drift
  apart. The shared `PricingCalculator` guarantees they can't; the extra query is small, low-cardinality
  reference data, and the handler's existing 30s `HybridCache` wrapper already absorbs its repeat-request
  cost.

## Consequences

- Every future new rule type needs new nullable columns on `PricingRule` (schema growth) rather than a
  new table - fine while the type count stays small (3-5); revisit the one-table-per-type alternative if
  it grows much further.
- `GetPriceCalendarHandler` goes from one Postgres round trip to two (the existing availability SQL, plus
  an EF query for the unit's active rules) per uncached calendar request.
- Rule write-time conflicts surface to hosts as a 409 (`PricingRuleConflictException`). The
  "at most one active length-of-stay rule" simplification means a host wanting tiered discounts (e.g. one
  rate at 7 nights, a deeper one at 30) cannot express that in v1 without deleting and replacing the
  existing rule - a deliberate scope cut, not an oversight, revisit if a real need for it shows up.
