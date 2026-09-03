namespace BuildingBlocks.Pagination;

/// <summary>
///     <see cref="PagedResponse{T}"/>'s sibling for endpoints whose filter is
///     too expensive to run twice.
///     <para>
///         The difference is one field, and it is the whole point.
///         PagedResponse promises a TotalCount, and the only way to keep that
///         promise is a COUNT that re-runs the entire WHERE clause beside the
///         page query. This shape promises only <see cref="HasNextPage"/>,
///         which a single query can answer by fetching one row more than it
///         returns.
///     </para>
///     <para>
///         Use it where the total is a "load more" signal rather than a number
///         anyone reads. Where a UI genuinely renders "Page 3 of 47" or
///         "1,284 results", the count is the product requirement and
///         PagedResponse is the right shape - paying for it is then the cost of
///         the feature, not waste.
///     </para>
///     <para>
///         Note what this does NOT solve: it removes the second execution of
///         the filter, not the offset scan in the first. Postgres still walks
///         and discards every row before OFFSET, which is why
///         <see cref="PaginationDefaults.MaxOffset"/> still applies. Paging
///         genuinely deep wants a keyset cursor; this is the cheap half of that
///         fix, and it is the half that pays on every request rather than only
///         on deep ones.
///     </para>
/// </summary>
public record PagedSliceResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }

    /// <summary>
    ///     Whether a further page exists. Exact, not an estimate - it is
    ///     observed by asking for one row past the page and seeing whether it
    ///     came back, so it cannot drift from what the next request will find
    ///     any more than a TotalCount taken a moment earlier could.
    /// </summary>
    public required bool HasNextPage { get; init; }
}
