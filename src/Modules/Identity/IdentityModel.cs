using Identity.Entities;
using Identity.Entities.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Identity;

/// <summary>
///     The Identity schema, written out rather than inherited from IdentityDbContext, because AppDbContext
///     cannot derive from it. Claim tables are left out: nothing in this app reads or writes claims.
/// </summary>
public sealed class IdentityModel : IModuleModel
{
    public void Configure(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.HasIndex(u => u.NormalizedUserName).IsUnique().HasDatabaseName("UserNameIndex");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex");
            user.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
            user.Property(u => u.UserName).HasMaxLength(256);
            user.Property(u => u.NormalizedUserName).HasMaxLength(256);
            user.Property(u => u.Email).HasMaxLength(256);
            user.Property(u => u.NormalizedEmail).HasMaxLength(256);
        });

        builder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable("roles");
            role.HasKey(r => r.Id);
            role.HasIndex(r => r.NormalizedName).IsUnique().HasDatabaseName("RoleNameIndex");
            role.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
            role.Property(r => r.Name).HasMaxLength(256);
            role.Property(r => r.NormalizedName).HasMaxLength(256);
        });

        builder.Entity<IdentityUserRole<Guid>>(userRole =>
        {
            userRole.ToTable("user_roles");
            userRole.HasKey(r => new { r.UserId, r.RoleId });

            userRole.HasOne<ApplicationUser>().WithMany().HasForeignKey(r => r.UserId).IsRequired();
            userRole.HasOne<IdentityRole<Guid>>().WithMany().HasForeignKey(r => r.RoleId).IsRequired();
        });

        builder.Entity<IdentityUserLogin<Guid>>(login =>
        {
            login.ToTable("user_logins");
            login.HasKey(l => new { l.LoginProvider, l.ProviderKey });

            login.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.UserId).IsRequired();
        });

        builder.Entity<IdentityUserToken<Guid>>(token =>
        {
            token.ToTable("user_tokens");
            token.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });

            token.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).IsRequired();
        });

        builder.Entity<RefreshToken>(refreshToken =>
        {
            refreshToken.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
            refreshToken.HasIndex(t => t.TokenHash).IsUnique();

            // RevokeFamilyAsync scans by family.
            refreshToken.HasIndex(t => t.FamilyId);

            // ExpiredRefreshTokensSweepJob scans by expiry, and deliberately ignores IsRevoked: a revoked
            // but unexpired token still carries reuse-detection value.
            refreshToken.HasIndex(t => t.ExpiresAt);
        });

        builder.ApplyConfiguration(new RoleConfiguration());
        builder.ApplyConfiguration(new UserConfiguration());
    }
}
