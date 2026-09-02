namespace BuildingBlocks.Pagination;

/// <summary>
///     The page-size bounds every paged request and validator shares.
///     <para>
///         These are contract limits, not query mechanics: request DTOs
///         default to <see cref="DefaultPageSize"/> and validators reject
///         anything past <see cref="MaxPageSize"/>. They used to sit on
///         PaginationExtensions, which has since moved to Persistence because
///         it needs EF Core - taking them along would have meant every paged
///         request shape depending on the data-access assembly to know how big
///         a page may be.
///     </para>
/// </summary>
public static class PaginationDefaults
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
}
