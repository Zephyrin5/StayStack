using Hosts.Features.CreateHost;
using System.Text.Json.Serialization;
namespace Hosts.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateHostRequest))]
[JsonSerializable(typeof(CreateHostResponse))]
public partial class HostsJsonSerializerContext : JsonSerializerContext;
