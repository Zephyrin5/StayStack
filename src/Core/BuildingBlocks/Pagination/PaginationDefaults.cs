namespace BuildingBlocks.Pagination;

/// <summary>
///     The page-size bounds every paged request and validator shares.
///     <para>
///         These are contract limits, not query mechanics: request DTOs
///         default to <see cref="DefaultPageSize"/> and validators reject
///         anything past <see cref="MaxPageSize"/> - as does the query helper
///         itself, so a paged caller that arrives without a validator is
///         bounded too. Here rather than beside PaginationExtensions, so request
///         shapes do not depend on the EF Core assembly.
///     </para>
/// </summary>
public static class PaginationDefaults
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    /// <summary>
    ///     The furthest row a caller may page to. Capping PageSize bounds the
    ///     response; only this bounds the work, because it is the OFFSET that
    ///     costs - Postgres plans, scans and discards every row before it, and
    ///     the accompanying count runs the full filter regardless.
    ///     <para>
    ///         100 pages of 100, which is past where anyone browses and well
    ///         short of where it hurts. Deep paging beyond this wants a keyset
    ///         cursor, not a bigger number here.
    ///     </para>
    /// </summary>
    public const int MaxOffset = 10_000;

    /// <summary>
    ///     Whether this page/size lands within <see cref="MaxOffset"/>.
    ///     <para>
    ///         Computed in long deliberately. page and pageSize are both int
    ///         and C# arithmetic is unchecked, so page=30000000 with
    ///         pageSize=100 wrapped 2,999,999,900 to about -1.29e9 - and a
    ///         negative OFFSET is an error Postgres raises, which surfaced as a
    ///         500 from an anonymous query string. Widening the type is not the
    ///         fix on its own, since a valid huge offset is just as expensive;
    ///         it is what lets the bound below be checked before the overflow
    ///         eats it.
    ///     </para>
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
