using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    // No seeded user, deliberately. A seeded administrator would give every
    // deployment that runs migrations a known-credential account holding the
    // Administrator role, with its hash readable in source control.
    //
    // RoleConfiguration still seeds the roles themselves, which is different
    // in kind: roles are reference data the app's authorization checks name
    // directly, and no one can sign in as one.
    //
    // Integration tests create their own administrator at startup with a
    // per-run password (see IntegrationTestAdmin), so the credential exists
    // only in the test database and only for the life of the run.
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
    }
}
