namespace Identity.Features.RemoveRole;

public record RemoveRoleResponse
{
    public Guid UserId { get; init; }
    public List<string> Roles { get; init; } = [];
}
