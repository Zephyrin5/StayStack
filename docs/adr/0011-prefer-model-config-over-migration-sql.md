# 0011 - Prefer EF model configuration over hand-written migration SQL

**Status:** Accepted

## Context

Anything expressed only as literal SQL inside a migration's `Up()` is invisible to EF Core's model: it is not diffed when future migrations are scaffolded, and regenerating a fresh `Initial` from the model would silently omit it. For a constraint that enforces an invariant, that loss is a correctness bug.

## Decision

**Default to EF Core fluent model configuration for schema. Use raw migration SQL only when the provider has no fluent API for what is needed - confirmed against the provider's issue tracker, not assumed.**

- `ix_transactions_booking_id_active` ("at most one `Pending`/`Succeeded` transaction per booking") is model configuration: `HasIndex(t => t.BookingId, "ix_transactions_booking_id_active").IsUnique().HasFilter(...)`. The migration that brought it into the model has empty `Up()`/`Down()`, because the index already existed physically.
- Partial unique indexes on pricing rules and promotions are model configuration for the same reason.
- The GiST exclusion constraint on `unit_availability_holds` ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)) and the pricing-rule date-range constraint ([ADR-0012](0012-single-pricing-rule-entity-with-write-time-overlap-rejection.md)) are raw SQL: Npgsql's EF provider has no fluent API for exclusion constraints ([npgsql/efcore.pg#1975](https://github.com/npgsql/efcore.pg/issues/1975)).

Two implementation gotchas:

- EF Core merges repeated `HasIndex(x => x.Prop)` calls on the same property into one index unless each is named at the call (`HasIndex(x => x.Prop, "explicit_name")`); an unnamed second call reconfigures the first.
- `EFCore.NamingConventions`' snake_case rename can override a name set only through a later `.HasDatabaseName(...)`. Pin the name at `HasIndex(...)` and also call `.HasDatabaseName(...)`.

## Alternatives considered

- **Raw migration SQL wherever convenient.** Rejected: a squash would silently and permanently drop an enforced invariant.
- **Forcing exclusion constraints into the model** (a raw `HasAnnotation`, a provider fork). Rejected: no supported mechanism, and the workaround would be more fragile than the SQL.

## Consequences

- Raw-SQL constraints are the schema a migration squash would lose. `SchemaInvariantsTests` and `PricingRuleConstraintTests` assert them against the live schema.
- A new constraint or index is checked against the provider's fluent API before defaulting to raw SQL.
- Constraints matched by name in code ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)) take their names from constants the configuration or migration also uses.
- If npgsql/efcore.pg#1975 is implemented, the exclusion constraints can move into the model.
