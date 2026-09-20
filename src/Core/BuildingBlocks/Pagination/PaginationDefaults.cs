namespace BuildingBlocks.Pagination;

/// <summary>
///     The page-size bounds every paged request and validator shares. Contract limits rather than
///     query mechanics, and enforced by the query helper too, so a caller arriving without a validator
///     is still bounded. Here rather than beside PaginationExtensions, so request shapes do not depend
///     on EF Core.
/// </summary>
public static class PaginationDefaults
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    /// <summary>
    ///     The furthest row a caller may page to. Capping PageSize bounds the response; only this
    ///     bounds the work, because Postgres scans and discards every row before the OFFSET. Deep
    ///     paging past 100 pages of 100 wants a keyset cursor, not a bigger number here.
    /// </summary>
    public const int MaxOffset = 10_000;

    /// <summary>
    ///     Whether this page/size lands within <see cref="MaxOffset"/>. Computed in long because both
    ///     arguments are int and C# arithmetic is unchecked: page=30000000, pageSize=100 wrapped to a
    ///     negative offset, which Postgres raises as an error - a 500 from an anonymous query string.
    /// </summary>
    public static bool IsOffsetWithinLimit(int page, int pageSize)
    {
        if (page < 1 || pageSize < 1)
        {
            return false;
        }

        long offset = ((long)page - 1) * pageSize;
        return offset <= MaxOffset;
    }
}
