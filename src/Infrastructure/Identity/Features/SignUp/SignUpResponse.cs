using BuildingBlocks.Observability;
namespace Identity.Features.SignUp;

public record SignUpResponse
{
    public Guid Id { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }

    [Sensitive] public string? AccessToken { get; init; }

    [Sensitive] public string? RefreshToken { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];
}
