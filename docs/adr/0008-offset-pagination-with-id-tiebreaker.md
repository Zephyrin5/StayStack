# 0008 - Offset pagination with an Id tiebreaker

**Status:** Accepted

## Context

Every list endpoint (`GetProperties`, `GetMyProperties`, `GetMyBookings`, `GetHostBookings`, `GetTransactions`) needs stable pagination: requesting page 2 shouldn't duplicate or skip a row that was already seen on page 1, and shouldn't skip a row that arrives between requests either. The natural sort key for most of these lists (`CreatedAt`, or no explicit sort at all) is not a total order - two rows can legitimately share the same timestamp, especially for entities created in the same request or the same seeding batch. `Skip`/`Take` on a non-unique sort has no guaranteed stable order between two executions of the same query.

## Decision

- Responses use a shared, generic `PagedResponse<T>` (`Items`, `Page`, `PageSize`, `TotalCount`) - one envelope shape for every list endpoint, not a bespoke shape per feature.
- Pagination is offset-based (`Page`/`PageSize`), not cursor-based.
- Every query orders by its real sort column (or none, if there isn't one) followed by `.ThenBy(x => x.Id)` - `Id` (a `Guid.CreateVersion7()`, so it's also roughly time-ordered) is used purely as a tiebreaker to make the sort a total order, not as the actual sort criterion. A bare `.OrderBy(x => x.SomeField)` without the `Id` tiebreaker is a latent pagination-stability bug waiting for two rows to tie.

## Alternatives considered

- **Cursor-based (keyset) pagination.** More resilient to concurrent inserts/deletes shifting page boundaries mid-browse, and generally the better choice for a feed that changes quickly under the reader. Not adopted: none of these lists are high-churn enough for offset pagination's known weakness (a row inserted/removed while paging can shift results by one) to matter in practice, and offset pagination gives callers a simpler contract (jump to page N, show total count) that fits list-and-filter UIs (property browsing, a host's own bookings) better than an opaque cursor would.
- **Skip the tiebreaker, rely on the primary sort column alone.** Rejected outright once the failure mode was traced through: without it, `Skip`/`Take` is not guaranteed to draw the same page boundary on two requests when the primary sort has ties, which duplicates or drops rows across pages - a real, user-visible bug, not a theoretical one.

## Consequences

- Any new list endpoint should default to this same shape: `PagedResponse<T>`, and `.OrderBy(...).ThenBy(x => x.Id)` (or just `.OrderBy(x => x.Id)` if there's no meaningful primary sort yet).
- If a future feature genuinely needs cursor-based pagination (a high-churn feed, infinite-scroll over rapidly-changing data), that's a deliberate, separate decision to make for that endpoint - not a reason to change this default for the lists that don't need it.
