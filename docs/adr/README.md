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

- **Status** - Accepted or Deprecated. When a decision changes, rewrite the ADR to the current decision rather than adding a superseding one; history lives in git.
- **Context** - the problem and the constraints in play.
- **Decision** - what was chosen.
- **Alternatives considered** - what else was on the table, and why it lost.
- **Consequences** - what this commits us to, including the costs, not just the benefits.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-native-aot-compatibility.md) | Native AOT compatibility as a design constraint | Accepted |
| [0002](0002-tickerq-for-background-jobs.md) | TickerQ for background jobs | Accepted |
| [0003](0003-cross-module-writes-commit-in-one-transaction.md) | Cross-module writes commit in one database transaction | Accepted |
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
| [0018](0018-property-local-business-dates.md) | Business dates resolve in the property's time zone, never UTC | Accepted |
| [0019](0019-total-count-is-opt-in-on-expensive-list-endpoints.md) | A total count is opt-in, not part of every paging envelope | Accepted |
| [0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md) | A checkout is a claim with a deadline, not a sale | Accepted |
| [0021](0021-availability-is-part-of-bookings.md) | Holds are part of Bookings | Accepted |
| [0022](0022-checkout-idempotency-keys.md) | Checkout idempotency keys | Accepted |
| [0023](0023-booking-management-sessions.md) | The management token is exchanged once, not carried everywhere | Accepted |
| [0024](0024-every-memory-cache-entry-declares-its-size.md) | Every IMemoryCache entry declares its size, in bytes | Accepted |
| [0025](0025-retried-work-is-built-inside-the-retry.md) | The retry protocol | Accepted |
| [0026](0026-stay-search-materialises-the-platform-wide-blocked-set.md) | Stay search materialises a platform-wide blocked set, and that is a known ceiling | Accepted (records a boundary) |
| [0027](0027-refunds-are-decided-once-from-a-durable-obligation.md) | Refunds are decided once, from a durable obligation | Accepted |
| [0028](0028-advisory-locks-and-lock-order.md) | Advisory locks and lock order | Accepted |
| [0029](0029-atomic-scopes-for-cross-module-work.md) | Atomic scopes for cross-module work | Accepted |
