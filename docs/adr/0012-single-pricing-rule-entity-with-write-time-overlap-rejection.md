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

**Overlapping active rules of the same type are rejected at write time**, rather than resolved with a
priority or tie-break at read time. `PricingCalculator` takes the first matching rule per night (and the
first matching length-of-stay rule per stay) from an unordered list, so "at most one active rule of a type
matches" is a precondition of the price being deterministic. A second matching row would throw nowhere; it
would make the price depend on row order.

- `DateRangeOverride`: no two active overrides for a unit may overlap.
- `DayOfWeekMultiplier`: no weekday may belong to two active multipliers for a unit.
- `LengthOfStayDiscount`: **at most one active rule per unit** - avoids threshold-overlap semantics (does a
  7-night rule "overlap" a 14-night one?) for a product need that does not call for tiered discounts.

**The schema enforces all three invariants**, because they hold or fail regardless of how a row arrives - a
handler, a data migration, an admin script, raw SQL:

| Rule type | Enforced by |
|---|---|
| `DateRangeOverride` | `pricing_rules_date_range_overlap_excl`: `EXCLUDE USING gist (unit_id WITH =, date_range WITH &&)`, partial on `rule_type = 'DateRangeOverride' AND status <> 2`. Raw migration SQL; `btree_gist` is enabled. |
| `LengthOfStayDiscount` | `ix_pricing_rules_unit_length_of_stay_active`: a partial unique index on `unit_id`. |
| `DayOfWeekMultiplier` | `ix_pricing_rules_unit_day_of_week_{0..6}_active`: seven partial unique indexes on `unit_id`, each filtered on `rule_type = 'DayOfWeekMultiplier' AND status <> 2 AND days_of_week @> ARRAY[d]`, plus `ck_pricing_rules_days_of_week_domain` keeping `days_of_week` non-empty and inside 0..6, since a day outside that range would escape every index. |

`status <> 2` excludes archived rules, so an archived rule never blocks its replacement.

**`CreatePricingRuleHandler` and `UpdatePricingRuleHandler` check overlap in memory first**, against the
unit's small set of active same-type rules, in the handler rather than the validator (database-dependent
checks belong in the handler, per `CreateUnitRequestValidator`'s convention). That check gives the host a
specific message; it cannot close a race, because two concurrent writers both pass it. The constraints decide
the race, and `PricingRuleOverlapChecker.IsOverlapViolation` maps the loser's violation, by constraint name,
to the same `PricingRuleConflictException` (409).

**The handlers run at the default isolation level.** That depends on every invariant being in the schema:
with any one of them application-only, Serializable would be the only thing stopping two concurrent writers
from both committing, and it would have to return.

## Alternatives considered

- **One table per rule type.** Rejected - three DbSets/configs, and every rule-loading call site (both
  handlers, the overlap check) would need three queries or a `UNION` instead of one.
- **A `Priority` field to resolve overlapping rules at read time.** Rejected for v1 - adds a whole
  tie-breaking UX and validation surface the product need ("simple, predictable host-set rules") does not
  require. Can be introduced later as an additive change without migrating existing rows.
- **Serializable isolation instead of constraints.** Rejected: it protects concurrent writers through these
  handlers only, not the read-path invariant against any other writer, and it costs a `40001` retry on
  ordinary contention.
- **An `intarray` exclusion constraint for day-of-week overlap.** Rejected: `intarray` is not enabled, and
  installing it redefines `&&`, `@>` and `<@` for every `int4[]` in the database with semantics that differ
  from the built-ins. The domain is seven fixed values, so per-day unique indexes express the same invariant
  with built-in operators, and a violation names the day that collided.
- **Normalising days of week into their own rows.** Rejected: a second table and a join on every pricing read,
  to express what seven partial indexes already hold.
- **Resolving prices in SQL inside `GetPriceCalendarHandler`'s Dapper query**, to avoid the extra EF round
  trip per calendar request. Rejected - two independent implementations of the same precedence logic (SQL and
  C#) free to drift apart. The shared `PricingCalculator` prevents that; the extra query reads small reference
  data, and the handler's 30s `HybridCache` absorbs repeat requests.

## Consequences

- Every future new rule type needs new nullable columns on `PricingRule` rather than a new table - fine while
  the type count stays small (3-5); revisit one-table-per-type if it grows much further.
- Every new rule type also needs its own schema constraint, recognised by name in `IsOverlapViolation`, before
  it can rely on the default isolation level.
- `GetPriceCalendarHandler` makes two Postgres round trips per uncached calendar request: the availability
  SQL, and an EF query for the unit's active rules.
- Rule conflicts surface to hosts as a 409. A host wanting tiered length-of-stay discounts (one rate at 7
  nights, a deeper one at 30) cannot express that without replacing the existing rule - a deliberate scope
  cut.
- The date-range constraint lives in raw migration SQL, which a migration squash regenerated from the model
  would lose.
- `PricingRuleConstraintTests` writes straight through the `DbContext`, bypassing the handlers, so it tests
  the schema. `PricingRuleConcurrencyTests` races each rule type concurrently through the handlers; it passes
  at Read Committed, and its day-of-week race fails if those indexes are not unique.
- Both handlers clear the change tracker at the top of their retried delegate, and `UpdatePricingRuleHandler`
  reads sibling rules with `AsNoTracking()`, so a retry never checks overlap against a tracked copy left by a
  rolled-back attempt.
- `GetPriceCalendarHandler` and `HoldAvailabilityHandler` both call `PricingCalculator` and round through the
  same per-currency rule at the same points, so they agree on which rule applies and on the final amount (see
  [ADR-0015](0015-money-value-type-for-currency-amounts.md)).
