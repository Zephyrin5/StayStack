# Architecture Decision Records

This folder records the *why* behind decisions that aren't obvious from the code alone - the kind of thing a comment can point to instead of re-explaining every time it's relevant, and the kind of thing a future maintainer (or a reviewer who wasn't around when the decision was made) would otherwise have to reconstruct from commit history.

## When to add one

Not every decision needs an ADR. Write one when:

- The decision affects more than one module, or constrains future choices (library selection, a cross-cutting pattern).
- A reasonable alternative was seriously considered and rejected - the ADR's job is to record *why not*, not just *what*.
- The same reasoning would otherwise end up copy-pasted across multiple files' comments.

Don't write one for a decision that's local to a single handler/class and already clear from a short inline comment.

## Format

Each ADR is a numbered markdown file: `NNNN-short-title.md`. Keep the shape simple:

- **Status** - Accepted, Superseded (link to the one that replaced it), or Deprecated.
- **Context** - the problem and the constraints in play.
- **Decision** - what was chosen.
- **Alternatives considered** - what else was on the table, and why it lost.
- **Consequences** - what this commits us to, including the costs, not just the benefits.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-native-aot-compatibility.md) | Native AOT compatibility as a design constraint | Accepted |
| [0002](0002-tickerq-for-background-jobs.md) | TickerQ for background jobs | Accepted |
| [0003](0003-compensating-actions-over-distributed-transactions.md) | Compensating actions and a transactional outbox for cross-module writes | Accepted; partially superseded by [0017](0017-durable-intent-records-for-cross-module-writes.md) |
| [0004](0004-module-boundaries-via-contracts-projects.md) | Module boundaries via per-module Contracts projects | Accepted |
| [0005](0005-host-is-a-capability-not-a-separate-account.md) | Host is a capability on an account, not a separate account type | Accepted |
| [0006](0006-materialize-then-map-for-jsonb-value-objects.md) | Materialize-then-map for JSONB-converted value objects | Accepted |
| [0007](0007-separate-requests-for-public-vs-owner-scoped-queries.md) | Separate Mediator requests for public vs. owner-scoped queries | Accepted |
| [0008](0008-offset-pagination-with-id-tiebreaker.md) | Offset pagination with an Id tiebreaker | Accepted |
| [0009](0009-refresh-token-rotation-with-family-reuse-detection.md) | Refresh-token rotation with family-based reuse detection | Accepted |
| [0010](0010-postgres-exclusion-constraint-for-double-booking.md) | Postgres exclusion constraint for double-booking prevention | Accepted |
| [0011](0011-prefer-model-config-over-migration-sql.md) | Prefer EF model configuration over hand-written migration SQL | Accepted |
| [0012](0012-single-pricing-rule-entity-with-write-time-overlap-rejection.md) | Single PricingRule entity with write-time overlap rejection | Accepted |
| [0013](0013-admin-targeted-host-queries-as-a-third-request-variant.md) | Admin-targeted host queries as a third request variant | Accepted |
| [0014](0014-ef-core-vs-dapper-decision-rule.md) | EF Core vs. Dapper: which owns a given database operation | Accepted |
| [0015](0015-money-value-type-for-currency-amounts.md) | A `Money` value type for currency amounts, at the domain boundary only | Accepted |
| [0016](0016-trust-model-for-anonymous-endpoints.md) | Trust model for anonymous endpoints | Accepted |
| [0017](0017-durable-intent-records-for-cross-module-writes.md) | Durable intent records for the forward half of cross-module writes | Accepted |
