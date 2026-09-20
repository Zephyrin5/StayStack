using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

/// <summary>
///     The "auth" policy: credential and token issuance endpoints, the
///     obvious credential-stuffing targets.
/// </summary>
public class AuthRateLimitOptions : IFixedWindowLimit
{
    // Its own section, nested under a RateLimiting parent that groups the
    // policies without merging them, so each policy has its own property
    // namespace rather than a name prefix nothing enforces.
    public const string SectionName = "RateLimiting:Auth";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
