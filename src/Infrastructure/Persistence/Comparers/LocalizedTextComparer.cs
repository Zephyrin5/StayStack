using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SeedWork.ValueObjects;
namespace Persistence.Comparers;

// The Comparer: Ensures EF Core detects changes inside the JSON
[UsedImplicitly]
public class LocalizedTextComparer() : ValueComparer<LocalizedText>(
    (c1, c2) => object.Equals(c1, c2),
    c => c.GetHashCode(),
    c => LocalizedText.Restore(c.Values.ToDictionary()));
