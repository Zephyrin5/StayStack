namespace BuildingBlocks.Pagination;

/// <summary>
///     <see cref="PagedResponse{T}"/>'s sibling for endpoints whose filter is too expensive to run
///     twice: it promises <see cref="HasNextPage"/> rather than a TotalCount, which one query answers
///     by fetching a row more than it returns.
///     <para>
///         Use PagedResponse where a UI renders "Page 3 of 47" - the count is then the feature. This
///         removes the second execution of the filter, not the offset scan in the first, so
///         <see cref="PaginationDefaults.MaxOffset"/> still applies.
///     </para>
/// </summary>
public record PagedSliceResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }

    /// <summary>Exact, not an estimate: observed by asking for one row past the page.</summary>
    public required bool HasNextPage { get; init; }
}
