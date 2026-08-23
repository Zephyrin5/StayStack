using BuildingBlocks.Observability;
namespace Identity.Features.SignIn;

public record SignInResponse
{
    public Guid Id { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }

    [Sensitive] public string? AccessToken { get; init; }

    [Sensitive] public string? RefreshToken { get; init; }

    public List<string>? Roles { get; init; }
}
