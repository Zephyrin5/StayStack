using SeedWork.Enums;
namespace Persistence;

/// <summary>
///     The soft-delete predicate as SQL, for the places EF's query filter cannot reach: raw
///     statements and partial index filters, which Postgres stores as text.
///     <para>
///         One definition rather than a literal <c>2</c> in each of them. The ordinal is the stored
///         value, so reordering <see cref="EntityStatus"/> would silently change what every one of
///         those filters means; here it is derived from the enum, and SoftDeleteFilterShapeTests pins
///         both the ordinal and the shape of the EF-side filter this restates.
///     </para>
/// </summary>
public static class SoftDelete
{
    public static readonly int ArchivedStatus = (int)EntityStatus.Archived;

    /// <summary>The predicate, for a statement or index filter with one table in scope.</summary>
    public static readonly string NotArchived = $"status <> {ArchivedStatus}";

    /// <summary>The same, qualified, for a statement that names more than one table.</summary>
    public static string NotArchivedOn(string alias) => $"{alias}.status <> {ArchivedStatus}";
}
