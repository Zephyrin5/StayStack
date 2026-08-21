using Api.Localization;
using System.Text.Json.Serialization;
namespace Api;

// Api's own contribution to the combined resolver in Program.cs - just the
// types that actually live here (LanguageDto). Everything else is each
// module's own JsonSerializerContext (see IdentityJsonSerializerContext,
// CatalogFeaturesJsonSerializerContext, HostsFeaturesJsonSerializerContext).
[JsonSerializable(typeof(LanguageDto))]
[JsonSerializable(typeof(List<LanguageDto>))]
public partial class AppJsonSerializerContext : JsonSerializerContext;
