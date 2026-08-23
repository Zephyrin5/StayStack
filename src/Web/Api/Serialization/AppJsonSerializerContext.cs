using Api.Localization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
namespace Api.Serialization;

// Api's own contribution to the combined resolver in Program.cs - the types
// that actually live here (LanguageDto), plus the two framework response
// shapes (ProblemDetails/ValidationProblemDetails) that GlobalExceptionHandler
// and the 404 status-code page write directly via WriteAsJsonAsync/
// Results.Problem, outside FastEndpoints' own request pipeline. Everything
// else is each module's own JsonSerializerContext (see
// IdentityJsonSerializerContext, CatalogFeaturesJsonSerializerContext,
// HostsFeaturesJsonSerializerContext).
[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LanguageDto))]
[JsonSerializable(typeof(List<LanguageDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
public partial class AppJsonSerializerContext : JsonSerializerContext;
