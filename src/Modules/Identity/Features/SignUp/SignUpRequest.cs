using BuildingBlocks.Observability;
using Mediator;
namespace Identity.Features.SignUp;

public record SignUpRequest : IRequest<SignUpResponse>
{
    public required string Email { get; init; }

    [Sensitive] public required string Password { get; init; }

    [Sensitive] public required string ConfirmPassword { get; init; }
}
