using BuildingBlocks.Security;
namespace Identity.Features.Common;

/// <summary>
///     A refresh token about to be issued: its row id and its plaintext, chosen
///     together and before anything is written.
///     <para>
///         Chosen by the caller rather than inside GenerateRefreshToken, and that
///         is the whole reason this type exists. RefreshTokenHandler rotates
///         inside a retried delegate; when both were minted inside it, a retry
///         after a commit that lost its acknowledgement held a different
///         replacement, could not recognise the one already committed, found the
///         presented token consumed - which is exactly what reuse looks like -
///         and revoked the family. Chosen once outside the retry, the id lets the
///         retry find its own rotation, and the plaintext lets it hand back the
///         token that row is the hash of.
///     </para>
/// </summary>
public sealed record IssuedRefreshToken(Guid Id, string Plaintext)
{
    public static IssuedRefreshToken New() => new IssuedRefreshToken(Guid.CreateVersion7(), SecureToken.Generate());
}
