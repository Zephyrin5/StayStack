using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    // No seeded user, deliberately.
    //
    // This used to HasData an administrator - admin@staystack.com, password
    // "1234", its hash pasted in as a literal - which meant every deployment
    // that ran migrations came up with a known-credential account holding the
    // Administrator role, in the schema itself. A shipped password is not a
    // development convenience once the migration is in the release; the
    // migration that created it has been in every environment this app has
    // ever been deployed to, and the hash is readable in source control.
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
