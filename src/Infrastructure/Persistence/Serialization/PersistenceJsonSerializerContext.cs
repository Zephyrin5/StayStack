using SeedWork.ValueObjects;
using System.Text.Json.Serialization;
namespace Persistence.Serialization;

[JsonSerializable(typeof(IDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(List<CancellationTier>))]
[JsonSerializable(typeof(IReadOnlyList<CancellationTier>))]
internal partial class PersistenceJsonSerializerContext : JsonSerializerContext;
