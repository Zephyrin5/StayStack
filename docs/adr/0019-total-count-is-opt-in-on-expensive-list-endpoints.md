# 0019 - A total count is opt-in, not part of every paging envelope

**Status:** Accepted

Amends [ADR-0008](0008-offset-pagination-with-id-tiebreaker.md), which made `PagedResponse<T>` - including its `TotalCount` - the single envelope for every list endpoint.

## Context

`ToPagedListAsync` issued a `CountAsync` on every call, unconditionally, to populate `PagedResponse<T>.TotalCount`. Two queries per paged request: one for the page, one for the total.

The two are not equally priced. The page query carries `LIMIT`/`OFFSET`; the count carries the same `WHERE` clause with no bound at all. On a cheap filter this is a rounding error. On `GetProperties` it is not - that filter is an escaped `ILIKE` over freeform city text plus an `EXISTS` over an array of candidate unit ids resolved from a separate Availability round trip (see [ADR-0004](0004-module-boundaries-via-contracts-projects.md) for why that array cannot be a correlated subquery). Serving the browse page meant running all of that twice per cache miss.

The count was paid for on the endpoint least able to afford it, and:

**nothing consumed the number.** `GetProperties` has exactly two callers. `PropertiesInfiniteList` feeds it to `getNextPagedParam`, which computes `page * pageSize < totalCount` and discards everything else. `getAllPropertiesForSitemap` walks pages until `all.length >= totalCount`. Both are asking one question - *is there another page* - and reconstructing it from a number neither of them displays. No properties view renders "1,284 results" or "Page 3 of 47".

The admin lists are the genuine counterexample: `TransactionsList` and `UsersList` render "Page 3 of 12 - 240 total" and disable Next from `page * pageSize >= totalCount`. They cannot work from a boolean, and their filters are ordinary indexed predicates.

So the question is not whether a total count is worth its cost. It is whether every list endpoint should pay for one whether or not it has a reader.

## Decision

**A total count is a product requirement of specific endpoints, not a property of pagination.** Endpoints that display a total keep `PagedResponse<T>` and its second query. Endpoints that only need to know whether more rows exist return `PagedSliceResponse<T>` - the same envelope with `TotalCount` replaced by `HasNextPage` - and answer it in one query.

`ToPagedSliceAsync` asks for `pageSize + 1` rows and returns `pageSize`. If the extra row came back there is another page; the row itself is discarded and re-fetched at the head of the next page. The extra row rides along in the scan the page query was already performing, so the marginal cost is a row rather than a query.

`GetProperties` is the only endpoint moved today. `GetMyProperties`, `GetHostProperties` and every other paged endpoint keep the counted envelope.

### Which envelope a new endpoint gets

**Slice where the count is both expensive and unused. Count everywhere else.** Both halves are required, and the conjunction is what stops this becoming a coin flip on the twelve-and-counting paged endpoints:

- *Expensive and unused* - `GetProperties`. Moved.
- *Expensive and displayed* - keep the count and pay for it; the number is a product requirement, and the alternative is lying about it. Consider a cheaper filter, or keyset, before considering a saturating total.
- *Cheap and unused* - **keep the count.** This is the case the rule exists to settle, and the answer is not the intuitive one. A `CountAsync` over an ordinary indexed predicate is close to free, while a second envelope shape is a permanent cost paid by every client, generated schema and consumer of that endpoint. Splitting the API to save a rounding error trades a cost that shows up in a profiler for one that shows up in every reader's head. It is also the case that describes most of the eleven endpoints that did not move.
- *Cheap and displayed* - keep the count. The overwhelming default.

The trigger is a filter expensive enough that executing it twice is a real cost - an unbounded `ILIKE`, a cross-module array, a correlated `EXISTS` - not merely a count with no current reader.

### The boolean is more accurate than the arithmetic it replaces

Not merely cheaper. `loaded < totalCount` compares a running client-side tally against a total measured by a different query, and goes wrong the moment a row is inserted or removed between two page fetches - which is why the sitemap walk carried a second `items.length === 0` check beside it. `HasNextPage` is observed by the query that returned the page, so it cannot disagree with that page.

### Both overloads share one offset guard

Skipping the count does not skip the `OFFSET`. `PaginationDefaults.MaxOffset` and its bounds check (see the overflow described in `IsOffsetWithinLimit`) apply identically to a slice query, and live in a single private method both overloads call. A regression test asserts the slice path rejects an out-of-bounds offset before issuing any command - removing the shared call reproduces the original `2201X: OFFSET must not be negative`.

## Alternatives considered

- **Keyset (cursor) pagination for `GetProperties`.** ADR-0008 named this as the answer if a list ever needed it, and the browse UI's forward-only infinite scroll is the shape keyset fits best. Not adopted *yet*, because it is the answer to a problem this endpoint does not currently have: keyset removes the `OFFSET` scan, which `MaxOffset` already bounds at 10,000 rows, and it would cost the page-numbered cache key, the server-rendered crawlable first page seeded from `page=1`, and the jump-to-page contract the admin lists rely on. Removing the duplicate filter execution is the cheaper half of that fix and the half that pays on *every* request rather than only on deep ones. If browse volume ever makes the offset scan itself the cost, keyset is the next step and this decision does not block it.
- **Cap the count instead - `query.Take(MaxOffset + pageSize).Count()`.** Bounds the count's work, since anything past `MaxOffset` is unreachable anyway. Rejected: it still executes the filter twice, which is the actual complaint, and it makes `TotalCount` silently saturate - a number that reads as exact and is not.
- **Make `TotalCount` nullable on the shared envelope.** One type, no new shape. Rejected: it pushes the question to every consumer at runtime rather than answering it in the contract, and a client that forgets the null case gets a broken pager rather than a compile error.
- **Keep the count everywhere and accept the cost.** The honest default, and it is what the admin lists still do. Rejected for `GetProperties` specifically because the cost there is the largest and the reader count is zero.

## Consequences

- **A breaking change to a public, anonymous endpoint.** `GET /api/catalog/properties` no longer returns `totalCount`. The generated client schema was regenerated and the two callers moved to `hasNextPage`.
- New paged endpoints follow the two-part rule above. `PagedResponse<T>` remains the default that ADR-0008 made it; `PagedSliceResponse<T>` is the exception, taken when a genuinely expensive filter would otherwise run twice for a number nothing reads. An earlier draft of this bullet made slicing the default whenever nothing rendered the total, which was a looser rule than the one actually applied - it would have moved most of the eleven untouched endpoints, and none of them were moved.
- **The API is inconsistent across list endpoints, deliberately, and clients see it.** `GET /api/catalog/properties` returns `hasNextPage` while every other paged endpoint returns `totalCount`, so a client cannot assume one paging shape. That is the cost of the rule above, accepted rather than overlooked: the alternative is either paying for a duplicated expensive filter on browse, or churning eleven endpoints and their consumers to a shape none of them needed. The split is kept legible by being decidable from the endpoint itself - one field's difference, one rule, and a reason recorded here - rather than by being uniform.
- `InfiniteList` is typed against `ItemsPage<T>` (`{ items }`) rather than `PagedResult<T>`, since it never read anything else. It works over both envelopes.
- Two envelope shapes now exist where there was one, which is a real cost in a codebase that deliberately shares one. It is bounded: the difference is a single field and the choice is decided by one question with an observable answer.
- **This does not bound the offset scan**, only the duplicate filter execution. `MaxOffset` remains the thing standing between an anonymous query string and a deep scan.
