using Identity;
using Identity.Entities;
using Identity.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
namespace IntegrationTests.Features.Auth;

[Collection("Integration Tests")]
public class ExpiredRefreshTokensSweepJobTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task SeedDatabaseAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SweepAsync_DeletesOnlyTokensPastExpiresAt_RevokedButUnexpiredSurvives()
    {
        // Arrange - deletes on ExpiresAt, never on IsRevoked: a revoked-but-
        // unexpired token still has reuse-detection value (see
        // ExpiredRefreshTokensSweepJob's own doc comment), so it must
        // survive the sweep even though it will never be presented again.
        DateTime now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        RefreshToken expired = new RefreshToken
        {
            TokenHash = "expired-token-hash",
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-31),
            ExpiresAt = now.AddDays(-1),
            IsRevoked = false
        };

        RefreshToken revokedButNotExpired = new RefreshToken
        {
            TokenHash = "revoked-not-expired-token-hash",
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-5),
            ExpiresAt = now.AddDays(25),
            IsRevoked = true,
            RevokedAt = now.AddDays(-1)
        };

        RefreshToken stillActive = new RefreshToken
        {
            TokenHash = "still-active-token-hash",
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            CreatedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(29),
            IsRevoked = false
        };

        await SeedDatabaseAsync(expired, revokedButNotExpired, stillActive);

        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        ExpiredRefreshTokensSweepJob job = new ExpiredRefreshTokensSweepJob(context, timeProvider);

        // Act
        await job.SweepAsync(null!, CancellationToken.None);

        // Assert
        List<Guid> remainingIds = await context.RefreshTokens
            .AsNoTracking()
            .Select(rt => rt.Id)
            .ToListAsync();

        Assert.DoesNotContain(expired.Id, remainingIds);
        Assert.Contains(revokedButNotExpired.Id, remainingIds);
        Assert.Contains(stillActive.Id, remainingIds);
    }
}
