using Mediator;
namespace Identity.Features.AssignRole;

public record AssignRoleRequest : IRequest<AssignRoleResponse>
{
    public Guid UserId { get; init; }
    public string Role { get; init; } = string.Empty;
}
