using Hosts.Features.CreateHost;
using System.Text.Json.Serialization;
namespace Hosts.Serialization;

// Named distinctly from the existing (internal, unrelated) HostJsonSerializerContext
// in this same namespace - that one covers Dictionary<string,string> for
// Host.DisplayName's own EF Core column conversion; this one is the wire
// contract for Hosts' FastEndpoints request/response DTOs. Different
// concern, deliberately not combined into one context.
[JsonSerializable(typeof(CreateHostRequest))]
[JsonSerializable(typeof(CreateHostResponse))]
public partial class HostsFeaturesJsonSerializerContext : JsonSerializerContext;
