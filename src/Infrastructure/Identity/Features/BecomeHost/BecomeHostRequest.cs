using Mediator;
namespace Identity.Features.BecomeHost;

public record BecomeHostRequest : IRequest<BecomeHostResponse>
{
    public required string BusinessName { get; init; }
    public required string ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
}
