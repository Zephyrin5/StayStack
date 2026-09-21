using Identity.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
namespace Identity.Features.Common;

/// <summary>
///     Checks an access token against the account it was issued for, so a role or host link taken
///     away stops being honoured within seconds rather than at the token's expiry (docs/adr/0030).
///     <para>
///         The stamp is <see cref="ApplicationUser" />'s own, moved by
///         <c>UserManager.UpdateSecurityStampAsync</c>. A token carries the value it was minted with;
///         a request is accepted only while that value is still the account's.
///     </para>
/// </summary>
internal static class SecurityStamps
{
    public const string ClaimType = "security_stamp";

    /// <summary>
    ///     How long a stale stamp can still be believed. The bound that matters is this one, not the
    ///     access token's lifetime: a change to the account clears the entry, and this is only what
    ///     remains when the clearing does not reach a process - another instance, or a crash between
    ///     the commit and the eviction.
    /// </summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(30);

    private static readonly HybridCacheEntryOptions CacheOptions = new HybridCacheEntryOptions
    {
        Expiration = CacheWindow,
        LocalCacheExpiration = CacheWindow
    };

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        string? presented = principal?.FindFirst(ClaimType)?.Value;

        if (!Guid.TryParse(principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out Guid userId)
            || string.IsNullOrEmpty(presented))
        {
            // Every token this application issues carries both (AuthTokenProvider). One that does
            // not was signed for something else, or predates the claim, and cannot be checked.
            context.Fail("The access token carries no account to check it against.");
            return;
        }

        IServiceProvider services = context.HttpContext.RequestServices;

        string stored = await services.GetRequiredService<HybridCache>().GetOrCreateAsync(
            KeyFor(userId),
            (Services: services, UserId: userId),
            static async (state, cancellationToken) =>
                await state.Services.GetRequiredService<IdentityDb>().Users.AsNoTracking()
                    .Where(user => user.Id == state.UserId)
                    .Select(user => user.SecurityStamp)
                    .SingleOrDefaultAsync(cancellationToken) ?? string.Empty,
            CacheOptions,
            cancellationToken: context.HttpContext.RequestAborted);

        // A deleted account reads as empty and matches nothing, so its tokens stop working too.
        if (!string.Equals(stored, presented, StringComparison.Ordinal))
        {
            context.Fail("The access token was issued before a change to this account.");
        }
    }

    /// <summary>
    ///     Drops the cached stamp for one account. Runs after the change to it has committed: an
    ///     eviction before the commit is undone by the next reader, which would still see the old row.
    /// </summary>
    public static ValueTask InvalidateAsync(HybridCache cache, Guid userId, CancellationToken cancellationToken) =>
        cache.RemoveAsync(KeyFor(userId), cancellationToken);

    private static string KeyFor(Guid userId) => $"identity:security-stamp:{userId:N}";
}
