using BuildingBlocks.Observability;
namespace Identity.Features.BecomeHost;

public record BecomeHostResponse
{
    public Guid HostId { get; init; }

    [Sensitive] public string? AccessToken { get; init; }

    [Sensitive] public string? RefreshToken { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];
}
