using BuildingBlocks.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<IdentityRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRole<Guid>> builder)
    {
        // ConcurrencyStamp is set explicitly on every row below - left
        // unset, IdentityRole<Guid>'s own constructor defaults it to
        // Guid.NewGuid().ToString(), which made this HasData call produce a
        // different model every time it was built (same failure mode as
        // the PasswordHash issue in UserConfiguration.cs, just a property
        // that's easier to miss since nothing else here looks random).
        builder.HasData(
            new IdentityRole<Guid>
            {
                Id = Guid.Parse("01a00be7-ddff-7598-bfaa-234b454b4029"),
                Name = AuthorizationPolicies.Administrator,
                NormalizedName = "ADMINISTRATOR",
                ConcurrencyStamp = "7cfd1da2-9854-4677-87ee-b6659ac06491"
            },
            new IdentityRole<Guid>
            {
                Id = Guid.Parse("01a00be7-ddff-7598-bfaa-256e7999a546"),
                Name = AuthorizationPolicies.Host,
                NormalizedName = "HOST",
                ConcurrencyStamp = "57622f33-b382-4978-928e-c7c362208dcc"
            },
            new IdentityRole<Guid>
            {
                Id = Guid.Parse("01a00be7-ddff-7598-bfaa-2b4b9a219feb"),
                Name = "PropertyStaff",
                NormalizedName = "PROPERTYSTAFF",
                ConcurrencyStamp = "b84be7ca-95b3-464a-9437-f152271e1f2d"
            },
            new IdentityRole<Guid>
            {
                Id = Guid.Parse("01a00be7-ddff-7598-bfaa-2de215ccf970"),
                Name = "Customer",
                NormalizedName = "CUSTOMER",
                ConcurrencyStamp = "f86f1503-e374-4bb8-9eae-405c9f4e0803"
            }
        );
    }
}
