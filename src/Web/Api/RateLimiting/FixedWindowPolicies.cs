using Api.Security;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
namespace Api.RateLimiting;

/// <summary>
///     The three policies differ only in their limits, so they are registered the same way once.
///     <para>
///         Every one partitions on <see cref="ClientNetworkKey"/>, the same key the hold cap counts by
///         (docs/adr/0016): derived from the peer address and never from anything the caller supplies,
///         and narrowed to a network so one client cannot spread a budget across addresses it owns.
///     </para>
/// </summary>
internal static class FixedWindowPolicies
{
    // The annotation is what IOptions<TOptions> asks of its type argument; without it this generic
    // hands the trimmer a type it cannot see the constructor of.
    public static RateLimiterOptions AddFixedWindow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOptions>(
        this RateLimiterOptions options, string policy)
        where TOptions : class, IFixedWindowLimit
    {
        options.AddPolicy(policy, httpContext =>
        {
            TOptions limits = httpContext.RequestServices.GetRequiredService<IOptions<TOptions>>().Value;

            // Read per partition rather than captured, so a test can change the limits of a running host.
            return RateLimitPartition.GetFixedWindowLimiter(
                ClientNetworkKey.Resolve(httpContext.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0
                });
        });

        return options;
    }
}

/// <summary>What the helper above needs from a policy's options.</summary>
public interface IFixedWindowLimit
{
    int PermitLimit { get; }

    int WindowSeconds { get; }
}
