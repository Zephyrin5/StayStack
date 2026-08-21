using System.Text.Json.Serialization;
namespace Persistence.Serialization;

[JsonSerializable(typeof(IDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal partial class PersistenceJsonSerializerContext : JsonSerializerContext;
