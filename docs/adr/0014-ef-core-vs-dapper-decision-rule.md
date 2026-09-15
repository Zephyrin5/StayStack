# 0014 - EF Core vs. Dapper: which owns a given database operation

**Status:** Accepted

## Context

The codebase uses both EF Core and Dapper. Without a stated rule the failure modes are reaching for Dapper on a path that is plain CRUD, adding mapping for nothing, or reaching for EF on a path that needs a Postgres-specific shape EF cannot translate (`generate_series`, a guarded `UPDATE ... RETURNING` in one round trip, an advisory lock).

## Decision

Three tiers, in order of preference. Drop to the next only when the one above cannot do the job.

**Tier 1 - EF Core LINQ and change tracking (`SaveChangesAsync`).** The default: CRUD on an aggregate and any read expressible as LINQ. Covers `Property`, `Unit`, `Booking`, `Transaction`, `ApplicationUser`, `RefreshToken` issuance, reviews, `PricingRule`, `Promotion` outside its redemption counter, `BookingManagementToken`, the intent and obligation rows. Concurrency safety on this tier still comes from a database constraint, matched by name ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)): `InitiateTransactionHandler`'s pre-check gives a friendly error, and `ix_transactions_booking_id_active` is the authority.

**Tier 2 - `ExecuteUpdateAsync` / `ExecuteDeleteAsync`.** A single-statement operation LINQ can express, used when tracking is pure overhead or when a conditional mutation needs atomicity a load-then-save cannot give. `AuthTokenProvider`'s consume-once `UPDATE ... WHERE !IsRevoked` and `RevokeFamilyAsync` are here for atomicity; `ExpiredRefreshTokensSweepJob` because there is nothing to load.

**Tier 3 - Dapper on `dbContext.Database.GetDbConnection()`.** Reserved for what LINQ cannot express or a Postgres construct EF has no translation for:

- `generate_series(...)` cross-joined against a table (`GetPriceCalendarHandler`).
- A guarded `UPDATE ... RETURNING` of several columns in one round trip (`HoldConfirmation.ConfirmHoldAsync`).
- An insert whose guarantee is a constraint EF has no fluent representation for - the GiST exclusion constraint behind `HoldAvailabilityHandler` ([ADR-0010](0010-postgres-exclusion-constraint-for-double-booking.md)).
- Advisory locks and bare row claims (`SELECT id ... FOR UPDATE`) whose result is not an entity ([ADR-0028](0028-advisory-locks-and-lock-order.md)).
- **Every write to `unit_availability_holds`**, including `ExpiredHoldsSweepJob`'s delete, which Tier 2 could express. One absolute rule for the one table where a mistake is a double booking is easier to audit than a rule with an exception.

Dapper statements inside an EF transaction are handed that transaction explicitly; Dapper does not discover it ([ADR-0021](0021-availability-is-part-of-bookings.md)).

**`FromSqlRaw` is used only to claim a row as a tracked entity** - `SELECT * ... FOR UPDATE SKIP LOCKED` in `OutboxDispatcherBase` and the reconcile jobs - where the claimed entity is then modified and saved. Where EF cannot materialise the raw row (`Booking`, whose `Money` complex property makes EF ask for a column the snake_case convention never produces), the id is claimed through Dapper and the entity loaded through LINQ.

**The split is per operation, not per table.** `PromotionRedemption.RedeemAsync` reads the promotion through LINQ and drops to Dapper only for its atomic increment-and-insert. A `DbSet<T>` shows which module owns a table's schema, not which tier its operations use.

**Raw SQL restates the soft-delete filter.** `ApplySoftDeleteQueryFilter` applies to LINQ only. A Tier 3 query over an `Entity`-derived table must restate `status <> @ArchivedStatus` by hand, with the value derived from `EntityStatus` (stored as its integer ordinal). `GetPriceCalendarHandler` does; without it an archived unit's calendar is returned and priced.

## Alternatives considered

- **EF everywhere.** Rejected: no LINQ translation for `generate_series`, no fluent API for exclusion constraints, no `RETURNING` in `ExecuteUpdateAsync`.
- **Dapper everywhere, EF only for migrations.** Rejected: discards change tracking, LINQ and migration diffing for the large majority of operations that are plain CRUD.
- **A strict per-table assignment.** Rejected: `PromotionRedemption` shows the right granularity is per operation.

## Consequences

- New code defaults to Tier 1, uses Tier 2 for a conditional single-statement mutation or a bulk delete/update, and drops to Tier 3 only when the provider's LINQ and fluent API are confirmed unable to express it - the same discipline [ADR-0011](0011-prefer-model-config-over-migration-sql.md) applies to migration SQL.
- Dapper type handlers (`CurrencyTypeHandler`, `DateOnlyTypeHandler`, `NpgsqlRangeTypeHandler`) are registered once in `PersistenceServicesRegistration`, so raw rows map enums, dates and ranges the same way EF does.
- `Bookings`, `Catalog` and `Hosts` carry a `[module: DapperAot]` attribute. Hosts issues no Dapper queries; the attribute has no effect without one.
