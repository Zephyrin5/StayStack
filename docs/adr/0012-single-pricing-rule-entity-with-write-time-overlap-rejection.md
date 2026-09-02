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
  Originally rejected - that mechanism exists specifically for the double-booking invariant under real
  concurrent write pressure (many guests racing to book the same unit). Pricing-rule authoring is a
  low-frequency, single-host admin action; an in-memory EF check keeps the logic in one readable place
  instead of requiring a raw-SQL migration for a concurrency profile that doesn't exist here.
  **This was later reversed - see "Amendment" below.**
- **Resolving prices in SQL inside `GetPriceCalendarHandler`'s existing Dapper query**, to avoid the
  extra EF round trip per calendar request. Rejected - it would mean two independent implementations of
  the same precedence logic (one in SQL, one in C# for `HoldAvailabilityHandler`), free to silently drift
  apart. The shared `PricingCalculator` guarantees they can't; the extra query is small, low-cardinality
  reference data, and the handler's existing 30s `HybridCache` wrapper already absorbs its repeat-request
  cost.

## Amendment: the overlap invariants are now enforced in the schema

The rejection recorded in "Alternatives considered" rested on a premise this
repository subsequently disproved, and on a framing that missed the stronger
argument.

**The premise didn't survive.** The entry says a GIST constraint was
unnecessary for "a concurrency profile that doesn't exist here." It does exist:
`PricingRuleConcurrencyTests` demonstrated that two concurrent conflicting
inserts could each pass their own overlap check, and both
`CreatePricingRuleHandler` and `UpdatePricingRuleHandler` had to be moved to
`IsolationLevel.Serializable` as a result. The comment now sitting above that
transaction says as much - "the check-then-insert below is still a genuine
read-then-write race with nothing at the database enforcing it."

**The framing missed the point.** Both the original entry and the Serializable
fix treat this as a concurrency question. It is really a read-path question.
`PricingCalculator` resolves a nightly price with `FirstOrDefault` over an
unordered `ToListAsync()` result, and does the same for the day-of-week and
length-of-stay lookups. "At most one rule matches" is therefore a precondition
for the calculator being deterministic at all - and a second matching row would
not throw anywhere, it would just make the price depend on row order. That is a
data invariant, and it holds or fails regardless of how the second row arrived:
a bulk import, a data migration, an admin script, a future handler that forgets
the checker, or anything issuing raw SQL. Serializable isolation protects
concurrent writers; it does not protect an invariant the read path depends on.

Two of the three invariants are now held by the schema, using only what was
already enabled:

- **`DateRangeOverride`** - `pricing_rules_date_range_overlap_excl`, an
  `EXCLUDE USING gist (unit_id WITH =, date_range WITH &&)` partial on
  `rule_type = 'DateRangeOverride' AND status <> 2`. `btree_gist` was already
  enabled for `unit_availability_holds`.
- **`LengthOfStayDiscount`** - `ix_pricing_rules_unit_length_of_stay_active`, a
  partial unique index on `unit_id`. "At most one per unit" is plain
  uniqueness, so this needs no exclusion constraint at all. This is the type
  where an unordered `FirstOrDefault` is most obviously wrong: two active rules
  with different `MinNights` both match a long stay, so the discount a guest
  receives would depend on row order.

**`DayOfWeekMultiplier` is deliberately still application-only.** Its invariant
is integer-array overlap, and Postgres has no built-in GiST opclass for
`integer[]`, so it cannot be an exclusion constraint without either enabling the
`intarray` extension or normalising days into their own rows. Neither is
justified by this finding alone, so the asymmetry is recorded rather than
papered over -
`PricingRuleConstraintTests.DayOfWeekMultiplier_OverlappingDaysForTheSameUnit_IsStillOnlyGuardedByApplicationCode`
asserts the gap explicitly so it stays known. That type is no worse protected
than the other two were before this change.

`PricingRuleOverlapChecker` stays exactly as it is. It produces a 409 with a
message a host can act on; the constraints are a backstop for writers that
never reach it. A backstop firing is genuinely exceptional, so it is left to
surface as a 500 rather than being translated into the same 409 - the same
reasoning `OrphanedUnitException` uses for a data-integrity violation.

The constraint tests write through `DbContext` directly, bypassing the checker
on purpose, and were verified against a build without the constraint: the
overlapping insert succeeds there, so they test the schema rather than
restating the application check.

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
- **The write-time overlap check was still a genuine in-memory race** (two concurrent `CreatePricingRule`
  calls could both load the same "no conflict" snapshot and both insert) - closed without contradicting
  the GIST-constraint rejection above: `CreatePricingRuleHandler` now wraps its check-then-insert in an
  explicit `IsolationLevel.Serializable` transaction rather than adding a constraint. Still no GIST, still
  plain EF, matching this ADR's own low-frequency/single-host reasoning - Postgres just refuses to let two
  concurrent serializable transactions both believe they saw the only version of the truth. Requires
  `40001` (serialization_failure) in `EnableRetryOnFailure`'s `errorCodesToAdd` alongside the existing
  `40P01` (deadlock) - without it, a genuine conflict here surfaces as an unhandled 500 instead of a
  retried transaction, the opposite of the intended fix.
- **`UpdatePricingRuleHandler` had the identical fix applied, but its retry path was genuinely broken until a
  concurrency test (not a single-threaded one - see this ADR's own reasoning for why that distinction
  matters) actually exercised it.** Its `existingSameType` query has no `AsNoTracking()`: on a retry after a
  real `40001`, EF's identity map hands back whatever instance of a sibling row is already tracked from the
  prior, rolled-back attempt - with that attempt's stale, pre-conflict values - instead of the row the
  repeated `SELECT` just fetched. A genuine write-skew race (two existing rules, each updated concurrently
  into a range that only conflicts with the *other's new* state, never its own original one - the textbook
  case Serializable exists to catch, that Read Committed or Repeatable Read would let through) reproduced
  this directly: the losing side's retry saw the sibling's superseded range and incorrectly succeeded. Fixed
  by adding `AsNoTracking()` to that one query - it's read-only data for the overlap check, never mutated,
  so it never needed the identity map at all. `CreatePricingRuleHandler` was never at risk of this specific
  failure mode (`ChangeTracker.Clear()` at the top of its own retry delegate already discards any stale
  tracked entries before `existingSameType` runs), which is exactly why only one of the two handlers needed
  this fix.
- **Both pricing paths now agree structurally *and* arithmetically.** The shared `PricingCalculator`
  already guaranteed `GetPriceCalendarHandler` and `HoldAvailabilityHandler` could never disagree on
  *which* rule applies - it didn't guarantee they'd land on the same final number, since neither path
  rounded before this session's `Money` work (see [ADR-0015](0015-money-value-type-for-currency-amounts.md)).
  Both now round through the same per-currency rule, at the same points, closing that gap.
