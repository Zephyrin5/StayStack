using System.Text.Json;
using System.Text.Json.Serialization;
namespace IntegrationTests;

// The API serializes every enum as its name, not its ordinal (see
// [JsonSourceGenerationOptions(UseStringEnumConverter = true)] on each
// module's JsonSerializerContext) - HttpContent.ReadFromJsonAsync's
// parameterless overload falls back to JsonSerializerOptions.Default, which
// still expects the BCL's numeric enum default and throws on a string
// payload. Pass this to every ReadFromJsonAsync call in this project so
// responses deserialize the way a real client - including this app's own
// generated TypeScript client - already does.
public static class TestJsonOptions
{
    public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
