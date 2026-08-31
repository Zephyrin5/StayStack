using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SeedWork.ValueObjects;
namespace Persistence.Comparers;

// Deep-equality comparer so EF Core detects in-place JSON mutations, not
// just reference changes.
[UsedImplicitly]
public class LocalizedTextComparer() : ValueComparer<LocalizedText>(
    (c1, c2) => object.Equals(c1, c2),
    c => c.GetHashCode(),
    c => LocalizedText.Restore(c.Values.ToDictionary()));
