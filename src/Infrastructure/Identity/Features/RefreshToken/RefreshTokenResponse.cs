using BuildingBlocks.Observability;
namespace Identity.Features.RefreshToken;

public record RefreshTokenResponse
{
    [Sensitive] public string AccessToken { get; init; } = string.Empty;

    // Nullable, not string.Empty-defaulted like AccessToken - a cookie-mode
    // caller's response has this explicitly nulled out by
    // RefreshTokenEndpoint (the new token goes in the rotated cookie
    // instead), so "no value" is a real, distinct state from "empty
    // string," not just an unset default.
    [Sensitive] public string? RefreshToken { get; init; }
}
