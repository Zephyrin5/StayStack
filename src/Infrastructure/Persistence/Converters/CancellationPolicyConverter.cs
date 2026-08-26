using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Persistence.Serialization;
using SeedWork.ValueObjects;
using System.Text.Json;
namespace Persistence.Converters;

[UsedImplicitly]
public class CancellationPolicyConverter() : ValueConverter<
    CancellationPolicy,
    string>(v => JsonSerializer.Serialize(v.Tiers, PersistenceJsonSerializerContext.Default.IReadOnlyListCancellationTier),
    v => CancellationPolicy.Restore(
        JsonSerializer.Deserialize<List<CancellationTier>>(v, PersistenceJsonSerializerContext.Default.ListCancellationTier)
        ?? new List<CancellationTier>()));
