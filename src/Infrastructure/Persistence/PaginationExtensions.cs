using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Persistence;

/// <summary>
///     Lives here rather than in BuildingBlocks because it is an EF Core
///     extension, and this is the project that owns EF Core. It was the only
///     thing in BuildingBlocks that needed that package - a Core project whose
///     charter is cross-cutting concerns with no infrastructure attached - and
///     the csproj comment justifying the reference there read as an apology
///     for it.
///     <para>
///         Only the query half moved. PagedResponse and
///         PaginationDefaults stay in BuildingBlocks: a response envelope and
///         two page-size bounds are contract shapes, consumed by request DTOs
///         and validators that have no business referencing a persistence
///         assembly.
///     </para>
/// </summary>
public static class PaginationExtensions
{
    // Skip/Take happens on the IQueryable<TEntity> - before mapping to a
    // summary DTO, not after - both for correctness (paginate the actual
    // rows, not an in-memory list) and because it's the only order that
    // lets the count and the page run as two ordinary queries against the
    // database instead of pulling every row over the wire first.
    public static async Task<(List<T> Items, int TotalCount)> ToPagedListAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        int offset = GuardOffsetAndCompute(page, pageSize);

        // Two queries, and the count is the expensive one: it re-runs the whole
        // WHERE clause without the LIMIT that bounds the page query. That is the
        // price of an exact TotalCount and it is worth paying wherever a caller
        // actually shows the number - the admin lists render "Page 3 of 12 - 240
        // total" and cannot do that from a boolean.
        //
        // Where the total is only feeding a "load more" decision, this is the
        // wrong overload: ToPagedSliceAsync answers that in one query.
        int totalCount = await query.CountAsync(cancellationToken);

        List<T> items = await query
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    ///     The same paging, one query instead of two, for callers that need to
    ///     know whether a next page exists but not how many rows there are in
    ///     total.
    /// </summary>
    /// <remarks>
    ///     Asks for pageSize + 1 rows and returns pageSize of them. If the
    ///     extra row came back there is more to fetch; the row itself is
    ///     discarded. That makes the "is there more" answer a property of the
    ///     page query rather than a second full execution of the filter.
    ///     <para>
    ///         The saving is the entire count. On a cheap filter that is a
    ///         rounding error and <see cref="ToPagedListAsync{T}"/> is worth
    ///         its extra information; on GetProperties, whose WHERE clause
    ///         includes an EXISTS over a candidate-unit id array, it halves the
    ///         work of every cache miss.
    ///     </para>
    ///     <para>
    ///         One row over is deliberate rather than one page over: the extra
    ///         row rides along in the same index scan Postgres was already
    ///         doing for the page, so the marginal cost is a row, not a query.
    ///     </para>
    /// </remarks>
    public static async Task<(List<T> Items, bool HasNextPage)> ToPagedSliceAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        int offset = GuardOffsetAndCompute(page, pageSize);

        // pageSize is at least 1 past the guard, but nothing bounds it from
        // above here - MaxPageSize is enforced by the request validators, not
        // by this method - so pageSize + 1 could overflow to negative and make
        // Take throw. Clamping instead reports no next page at a page size of
        // int.MaxValue, which is correct for any result set that can exist.
        int probeSize = pageSize < int.MaxValue ? pageSize + 1 : pageSize;

        List<T> items = await query
            .Skip(offset)
            .Take(probeSize)
            .ToListAsync(cancellationToken);

        bool hasNextPage = items.Count > pageSize;
        if (hasNextPage)
        {
            items.RemoveAt(items.Count - 1);
        }

        return (items, hasNextPage);
    }

    // Shared so the two overloads cannot drift on the bound that matters. A
    // slice query skips the count but still issues an OFFSET, so it needs this
    // guard for exactly the same reason.
    private static int GuardOffsetAndCompute(int page, int pageSize)
    {
        // Before any query, deliberately: a rejected page must cost no database
        // work at all. It used to run after the count, so a caller asking for
        // page 20,000,000 paid for the count before anything looked at the
        // offset.
        //
        // The offset is what needed bounding, not the page. page and pageSize
        // are both int and C# arithmetic is unchecked, so (page - 1) * pageSize
        // silently overflowed: page=30000000 with pageSize=100 wrapped
        // 2,999,999,900 to about -1.29e9, and Postgres refuses a negative
        // OFFSET - a 500 reachable from an anonymous query string. Below that
        // threshold nothing overflowed and it was still a scan of two billion
        // rows to discard them all.
        //
        // Here rather than only in the twelve request validators, because this
        // is the one place the arithmetic happens and a thirteenth paged
        // endpoint cannot forget it.
        if (!PaginationDefaults.IsOffsetWithinLimit(page, pageSize))
        {
            throw new ValidationException(
                nameof(page),
                $"Page and PageSize together may not skip past {PaginationDefaults.MaxOffset} results. " +
                "Narrow the search rather than paging deeper.");
        }

        // Safe to narrow: IsOffsetWithinLimit has already bounded this well
        // inside int range.
        return (int)(((long)page - 1) * pageSize);
    }
}
