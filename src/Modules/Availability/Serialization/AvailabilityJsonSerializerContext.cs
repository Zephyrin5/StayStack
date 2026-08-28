using Availability.Features.HoldAvailability;
using System.Text.Json.Serialization;
namespace Availability.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HoldAvailabilityRequest))]
[JsonSerializable(typeof(HoldAvailabilityResponse))]
public partial class AvailabilityJsonSerializerContext : JsonSerializerContext;
