# 0019 - A total count is opt-in, not part of every paging envelope

**Status:** Accepted

## Context

[ADR-0008](0008-offset-pagination-with-id-tiebreaker.md) defines offset pagination. A total count costs a second query carrying the page query's `WHERE` clause with no bound. On an ordinary indexed filter that is close to free. On `GetProperties` it is not: the filter is an escaped `ILIKE` over free-text city plus an exclusion of units blocked for the requested dates, resolved through a cross-module lookup ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)), so a count runs all of it twice per cache miss.

Readers differ too. The properties browse list and the sitemap walk ask only whether another page exists. The admin lists (`TransactionsList`, `UsersList`) render "Page 3 of 12 - 240 total" and cannot work from a boolean.

## Decision

**A total count is a product requirement of specific endpoints, not a property of pagination.**

- `PagedResponse<T>` (`ToPagedListAsync`) carries `TotalCount` and costs two queries. It is the default.
- `PagedSliceResponse<T>` (`ToPagedSliceAsync`) carries `HasNextPage` instead and costs one: it asks for `pageSize + 1` rows and returns `pageSize`. The extra row rides in the scan the page query already performs.

`GetProperties` returns a slice. Every other paged endpoint returns a count.

### Which envelope a new endpoint gets

**Slice only where the count is both expensive and unread.** Both halves are required:

| Filter | Total displayed | Envelope |
|---|---|---|
| Expensive | No | Slice |
| Expensive | Yes | Count - consider a cheaper filter or keyset before a saturating total |
| Cheap | No | **Count** |
| Cheap | Yes | Count |

The cheap-and-unread case keeps the count deliberately: a count over an indexed predicate is nearly free, while a second envelope shape is a cost paid by every client, generated schema and reader of that endpoint. "Expensive" means a filter whose second execution is a real cost - an unbounded `ILIKE`, a cross-module set, a correlated `EXISTS` - not merely a count with no current reader.

### `HasNextPage` is observed by the query that returned the page

`loaded < totalCount` compares a client-side tally against a total measured by a different query, and goes wrong when a row is inserted or removed between page fetches. `HasNextPage` cannot disagree with the page it came from.

### Both methods share the offset guard

Skipping the count does not skip the `OFFSET`. `PaginationDefaults.MaxOffset` and `IsOffsetWithinLimit` apply to both methods through one private guard, checked before any query. A test asserts the slice path rejects an out-of-bounds offset without issuing a command.

## Alternatives considered

- **Keyset pagination for `GetProperties`.** It fits forward-only infinite scroll best, but removes the `OFFSET` scan, which `MaxOffset` already bounds, at the cost of the page-numbered cache key, the server-rendered first page and jump-to-page. Removing the duplicate filter execution pays on every request. Keyset remains the next step if the offset scan itself becomes the cost.
- **Cap the count (`Take(MaxOffset + pageSize).Count()`).** Rejected: the filter still runs twice, and `TotalCount` silently saturates while reading as exact.
- **Nullable `TotalCount` on one envelope.** Rejected: it moves the question from the contract to every consumer at runtime.
- **A count everywhere.** Rejected for `GetProperties` only, where the cost is largest and nothing reads the number.

## Consequences

- **List endpoints differ, visibly.** `GET /api/catalog/properties` returns `hasNextPage`; every other paged endpoint returns `totalCount`. Clients cannot assume one paging shape. The difference is one field, decided by one rule recorded here.
- The client's `InfiniteList` is typed against `ItemsPage<T>` (`{ items }`) and works over both envelopes.
- **A slice does not bound the offset scan**, only the duplicate filter execution. `MaxOffset` bounds the scan.
