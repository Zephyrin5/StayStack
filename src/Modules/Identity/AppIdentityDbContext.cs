using Identity.Entities;
using Identity.Entities.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Outbox;
namespace Identity;

public class AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    // See AppBookingsDbContext.BookingsOutboxMessages for why this is
    // module-prefixed rather than just "OutboxMessages".
    public DbSet<OutboxMessage> IdentityOutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Ignore<IdentityRoleClaim<Guid>>();
        builder.Ignore<IdentityUserClaim<Guid>>();
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new OutboxMessageConfiguration());

        builder.Entity<RefreshToken>(entity =>
        {
            // Sets max length in the database schema (e.g., VARCHAR(64) / NVARCHAR(64))
            entity.Property(e => e.TokenHash)
                .IsRequired()
                .HasMaxLength(64);

            // Adds a unique index for lightning-fast lookups
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
        builder.ApplyConfiguration(new UserRoleConfiguration());

        // Convert names to snake case
        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
    }
}
