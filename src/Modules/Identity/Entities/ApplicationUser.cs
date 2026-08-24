using Microsoft.AspNetCore.Identity;
namespace Identity.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public override Guid Id { get; set; } = Guid.CreateVersion7();

    // Null for pure Customers - set once via BecomeHost. See docs/adr/0005
    // for why this is one account with an optional Host link, not a
    // separate Host account type.
    public Guid? HostId { get; set; }
}
