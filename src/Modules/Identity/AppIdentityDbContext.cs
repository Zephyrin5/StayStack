using Identity.Entities;
using Identity.Entities.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace Identity;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
public class AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Ignore<IdentityRoleClaim<Guid>>();
        builder.Ignore<IdentityUserClaim<Guid>>();
        base.OnModelCreating(builder);


        builder.Entity<RefreshToken>(entity =>
        {
            entity.Property(e => e.TokenHash)
                .IsRequired()
                .HasMaxLength(64);

            entity.HasIndex(e => e.TokenHash)
                .IsUnique();

            // RevokeFamilyAsync's WHERE FamilyId == ... AND !IsRevoked scan.
            entity.HasIndex(e => e.FamilyId);

            // ExpiredRefreshTokensSweepJob's WHERE ExpiresAt < now() scan -
            // not partial on IsRevoked, since the sweep deliberately
            // ignores it too (a revoked-but-unexpired token still has
            // reuse-detection value, so only ExpiresAt determines whether
            // a row is a cleanup target).
            entity.HasIndex(e => e.ExpiresAt);
        });

        builder.ApplyConfiguration(new RoleConfiguration());
        builder.ApplyConfiguration(new UserConfiguration());

        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
    }
}
