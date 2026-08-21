using BuildingBlocks.Observability;
using Mediator;
namespace Identity.Features.Auth.RefreshToken;

public record RefreshTokenRequest : IRequest<RefreshTokenResponse>
{
    [Sensitive] public required string RefreshToken { get; init; }
}
