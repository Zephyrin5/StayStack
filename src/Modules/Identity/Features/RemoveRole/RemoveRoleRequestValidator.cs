using FastEndpoints;
using FluentValidation;
namespace Identity.Features.RemoveRole;

public sealed class RemoveRoleRequestValidator : Validator<RemoveRoleRequest>
{
    public RemoveRoleRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Role).NotEmpty();
    }
}
