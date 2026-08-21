using Microsoft.AspNetCore.Identity;
namespace Identity.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public override Guid Id { get; set; } = Guid.CreateVersion7();

    // Null for pure Customers - set once via BecomeHost. One account, one
    // optional Host link, not a separate Host account type - matches how
    // Airbnb itself models this (see chat notes: "become a host" adds
    // capability to an existing account, it isn't a separate signup).
    public Guid? HostId { get; set; }
}
