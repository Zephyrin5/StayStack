using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Persistence.Serialization;
using SeedWork.ValueObjects;
using System.Text.Json;
namespace Persistence.Converters;

public class LocalizedTextConverter() : ValueConverter<
    LocalizedText,
    string>(v => JsonSerializer.Serialize(v.Values, PersistenceJsonSerializerContext.Default.IReadOnlyDictionaryStringString),
    v => LocalizedText.Restore(
        JsonSerializer.Deserialize<IDictionary<string, string>>(v, PersistenceJsonSerializerContext.Default.IDictionaryStringString)
        ?? new Dictionary<string, string>()));
