# 0011 - Prefer EF model configuration over hand-written migration SQL

**Status:** Accepted

## Context

Two pieces of Postgres-specific DDL exist in this schema: the GIST exclusion constraint on `unit_availability_holds` ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)) and, previously, a partial unique index enforcing "at most one active (`Pending`/`Succeeded`) transaction per booking" (`ix_transactions_booking_id_active`). Both were originally written as raw `migrationBuilder.Sql(...)` calls rather than EF Core fluent configuration.

That's a real, discovered risk, not just a style preference: anything expressed only as literal SQL inside one migration's `Up()` is invisible to EF Core's model - it's not part of what gets diffed to scaffold future migrations. Deleting all migrations and regenerating a fresh `Initial` from the current model would silently omit it, since the model has no memory of SQL that was only ever hand-inserted into a past migration file.

## Decision

Default to EF Core fluent model configuration for schema, and only fall back to raw migration SQL when the provider genuinely has no fluent API for what's needed - confirmed against the provider's own issue tracker, not assumed.

Applied concretely: the transactions partial unique index moved into `TransactionConfiguration` via `HasIndex(t => t.BookingId, "ix_transactions_booking_id_active").IsUnique().HasFilter(...)` - Npgsql's EF Core provider does support partial/filtered indexes through `HasFilter`, so there was no reason for this one to stay as raw SQL. The migration that made this move has deliberately empty `Up()`/`Down()` methods - the index already existed physically from the original raw-SQL migration; the point of that migration is only to bring the tracked model snapshot in line with reality, not to re-run DDL. The GIST exclusion constraint stays as raw SQL, because there's confirmed to be no fluent alternative ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)).

Two implementation gotchas worth recording so they aren't rediscovered the hard way:

- EF Core merges repeated `HasIndex(x => x.Prop)` calls on the same property into a single index unless each is named at the call site itself (`HasIndex(x => x.Prop, "explicit_name")`) - a second call without this silently reconfigures the first index rather than creating a second one.
- `EFCore.NamingConventions`' snake_case rename can still override an index name set only via a later chained `.HasDatabaseName(...)` - pin the name at the `HasIndex(...)` call itself, and also call `.HasDatabaseName(...)` to protect it from the naming convention overriding it a second time.

## Alternatives considered

- **Leave both as raw migration SQL, treat it as a wash.** Rejected once the actual risk was traced through: a squash silently and permanently losing an enforced invariant is a correctness bug waiting to happen, not a cosmetic inconsistency.
- **Move the exclusion constraint into the model too, by whatever means necessary (a raw `HasAnnotation`, forking the provider, etc.).** Not attempted - there's no supported mechanism, and hand-rolling one would be exactly the kind of fragile workaround this ADR is trying to avoid introducing elsewhere.

## Consequences

- The exclusion constraint remains the one piece of schema a full migration squash would silently lose - mitigated by `SchemaInvariantsTests` asserting it exists in the live schema, not by trying to force it into the model.
- Any new constraint/index should be checked against the target provider's actual fluent API support before defaulting to raw SQL, not the other way around.
