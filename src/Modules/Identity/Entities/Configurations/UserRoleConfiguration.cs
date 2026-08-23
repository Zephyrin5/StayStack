using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.HasData(
            new IdentityUserRole<Guid>
            {
                RoleId = Guid.Parse("01a00be7-ddff-7598-bfaa-234b454b4029"),
                UserId = Guid.Parse("01a00c51-f14f-750e-a0a1-c9f4d4e0f3b5")
            }
        );
    }
}
