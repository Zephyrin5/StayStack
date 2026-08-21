using BuildingBlocks.Observability;
using Identity.Features.SignUp;
using Mediator;
namespace Identity.Features.Auth.SignUp;

public record SignUpRequest : IRequest<SignUpResponse>
{
    public required string Email { get; init; }

    [Sensitive] public required string Password { get; init; }

    [Sensitive] public required string ConfirmPassword { get; init; }
}
