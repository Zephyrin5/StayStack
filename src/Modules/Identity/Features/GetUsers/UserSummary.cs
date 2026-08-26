namespace Identity.Features.GetUsers;

public record UserSummary
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public List<string> Roles { get; init; } = [];

    // Present only once BecomeHost has completed - same nullability
    // reasoning as ApplicationUser.HostId itself. Lets the client show a
    // "view host portal" link only for users who actually have one.
    public Guid? HostId { get; init; }
}
