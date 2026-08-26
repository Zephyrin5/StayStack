using FastEndpoints;
using FluentValidation;
namespace Identity.Features.AssignRole;

public sealed class AssignRoleRequestValidator : Validator<AssignRoleRequest>
{
    public AssignRoleRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Role).NotEmpty();
    }
}
