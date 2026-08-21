using BuildingBlocks.Observability;
namespace Identity.Features.Auth.RefreshToken;

public record RefreshTokenResponse
{
    [Sensitive] public string AccessToken { get; init; } = string.Empty;

    [Sensitive] public string RefreshToken { get; init; } = string.Empty;
}
