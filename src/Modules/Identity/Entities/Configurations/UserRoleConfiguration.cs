using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    // Nothing to seed: this existed only to give the seeded administrator its
    // role, and that account is gone. See UserConfiguration.
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
    }
}
