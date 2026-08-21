using Mediator;
namespace Hosts.Features.CreateHost;

public record CreateHostRequest : IRequest<CreateHostResponse>
{
    public string BusinessName { get; init; } = string.Empty;
    public string ContactEmail { get; init; } = string.Empty;
    public string? ContactPhone { get; init; }

    // Optional - Host.DisplayName is nullable for exactly this reason (see
    // Host.cs). Null/empty means "no customization yet", falls back to
    // BusinessName wherever it's displayed.
    public Dictionary<string, string>? DisplayName { get; init; }
}
