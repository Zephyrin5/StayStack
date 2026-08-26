using Mediator;
namespace Identity.Features.RemoveRole;

public record RemoveRoleRequest : IRequest<RemoveRoleResponse>
{
    public Guid UserId { get; init; }
    public string Role { get; init; } = string.Empty;
}
