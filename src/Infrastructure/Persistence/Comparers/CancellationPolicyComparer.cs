using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SeedWork.ValueObjects;
namespace Persistence.Comparers;

[UsedImplicitly]
public class CancellationPolicyComparer() : ValueComparer<CancellationPolicy>(
    (c1, c2) => object.Equals(c1, c2),
    c => c.GetHashCode(),
    c => CancellationPolicy.Restore(c.Tiers.ToList()));
