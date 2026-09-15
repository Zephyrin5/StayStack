using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Persistence;

/// <summary>
///     An EF Core extension, so it lives in the project that owns EF Core.
///     PagedResponse and PaginationDefaults stay in BuildingBlocks: they are
///     contract shapes, consumed by request DTOs and validators that should not
///     reference a persistence assembly.
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

        // pageSize + 1 cannot overflow: the guard above bounds it at
        // MaxPageSize.
        List<T> items = await query
            .Skip(offset)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        bool hasNextPage = items.Count > pageSize;
        if (hasNextPage)
        {
            items.RemoveAt(items.Count - 1);
        }

        return (items, hasNextPage);
    }

    // Shared so the two overloads cannot drift on the bounds that matter. A
    // slice query skips the count but still issues an OFFSET, so it needs
    // these guards for exactly the same reason.
    private static int GuardOffsetAndCompute(int page, int pageSize)
    {
        // Before any query: a rejected page costs no database work.
        //
        // The offset is what needs bounding. page and pageSize are int and C#
        // arithmetic is unchecked, so (page - 1) * pageSize can wrap negative
        // (page=30000000, pageSize=100 gives about -1.29e9), and Postgres
        // refuses a negative OFFSET - a 500 from an anonymous query string.
        // Below that it is still a scan of billions of rows to discard.
        //
        // Here as well as in the request validators, because this is where the
        // arithmetic happens and a new paged endpoint cannot forget it.
        if (!PaginationDefaults.IsOffsetWithinLimit(page, pageSize))
        {
            throw new ValidationException(
                nameof(page),
                $"Page and PageSize together may not skip past {PaginationDefaults.MaxOffset} results. " +
                "Narrow the search rather than paging deeper.");
        }

        // The offset check above does not bound pageSize on its own: at
        // page 1 the offset is 0 for any size, int.MaxValue included. So the
        // ceiling is checked here for the same reason the offset is, rather
        // than being left to the twelve validators - and it is what lets
        // ToPagedSliceAsync add its probe row without arithmetic that can
        // overflow.
        if (pageSize > PaginationDefaults.MaxPageSize)
        {
            throw new ValidationException(
                nameof(pageSize),
                $"PageSize may not exceed {PaginationDefaults.MaxPageSize}.");
        }

        // Safe to narrow: IsOffsetWithinLimit has already bounded this well
        // inside int range.
        return (int)(((long)page - 1) * pageSize);
    }
}
