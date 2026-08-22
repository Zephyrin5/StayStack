using Hosts.Features.CreateHost;
using System.Text.Json.Serialization;
namespace Hosts.Serialization;

[JsonSerializable(typeof(CreateHostRequest))]
[JsonSerializable(typeof(CreateHostResponse))]
public partial class HostsJsonSerializerContext : JsonSerializerContext;
