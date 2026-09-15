using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    // Nothing to seed: no user is seeded (see UserConfiguration).
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
    }
}
