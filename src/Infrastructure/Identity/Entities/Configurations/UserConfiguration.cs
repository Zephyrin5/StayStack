using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Identity.Entities.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.HasData(
            new ApplicationUser
            {
                Id = Guid.Parse("01a00c51-f14f-750e-a0a1-c9f4d4e0f3b5"),
                Email = "admin@staystack.com",
                NormalizedEmail = "ADMIN@STAYSTACK.COM",
                UserName = "admin",
                NormalizedUserName = "ADMIN",
                // Password is "1234" - hashed via PasswordHasher<ApplicationUser>
                // once and pasted here as a literal. HasData runs at model-build
                // time on every startup, and HashPassword salts randomly on each
                // call - a fresh hash every build made EF see the model as
                // "changing" relative to this migration's frozen snapshot,
                // which Database.Migrate() then refused to run against.
                PasswordHash = "AQAAAAIAAYagAAAAEOwwat4brQdWXuI6BwAY37PmMmCAc6UjfADvztT2EEXv2r3ynbvkfOJsrCIZYVzDEA==",
                EmailConfirmed = true,
                SecurityStamp = "9F8A526F-C2F0-4B39-83BE-C7D30C4E77BF",
                ConcurrencyStamp = "9F8A526F-C2F0-4B39-83BE-C7D30C4E77BF",
                TwoFactorEnabled = false,
                LockoutEnabled = false
            }
        );
    }
}
