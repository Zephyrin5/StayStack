using Microsoft.EntityFrameworkCore;
using TickerQ.Utilities.Base;
namespace Identity.Jobs;

/// <summary>
///     Deletes on ExpiresAt, never on IsRevoked - a revoked-but-not-yet-
///     expired token still needs to exist so a replay of it hits
///     AuthTokenProvider.ValidateRefreshToken's "already revoked" branch
///     (family-wide revocation) instead of "doesn't exist" (silently
///     ignored). Only a token past its own ExpiresAt has no further reuse-
///     detection value left to preserve.
/// </summary>
public class ExpiredRefreshTokensSweepJob(AppIdentityDbContext dbContext, TimeProvider timeProvider)
{
    [TickerFunction(functionName: "Identity.SweepExpiredRefreshTokens", cronExpression: "0 3 * * *")]
    public async Task SweepAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        await dbContext.RefreshTokens
            .Where(rt => rt.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
