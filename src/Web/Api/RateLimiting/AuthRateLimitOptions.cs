using System.ComponentModel.DataAnnotations;
namespace Api.RateLimiting;

/// <summary>
///     The "auth" policy: credential and token issuance endpoints, the
///     obvious credential-stuffing targets.
/// </summary>
public class AuthRateLimitOptions
{
    // Its own section, nested under a RateLimiting parent that groups the
    // policies without merging them.
    //
    // All three policies used to bind this parent directly, distinguished
    // only by prefixing their property names - AuthPermitLimit,
    // HoldPermitLimit, ReadPermitLimit - which made a naming convention
    // load-bearing with nothing enforcing it. A fourth policy added without
    // the prefix would have silently collided with whichever one it matched,
    // and the section itself was misleading to read: someone looking for the
    // read-endpoint limits would look for a section named after them and
    // find the keys filed under a different policy's name. Separate sections
    // cost a few lines of JSON and give each policy its own property
    // namespace back.
    public const string SectionName = "RateLimiting:Auth";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
