namespace Identity.Features.AssignRole;

public record AssignRoleResponse
{
    public Guid UserId { get; init; }
    public List<string> Roles { get; init; } = [];
}
