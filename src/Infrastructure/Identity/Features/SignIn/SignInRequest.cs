using BuildingBlocks.Observability;
using Mediator;
namespace Identity.Features.SignIn;

public record SignInRequest : IRequest<SignInResponse>
{
    public required string Email { get; init; }

    [Sensitive] public required string Password { get; init; }
}
