using BuildingBlocks.Security;
namespace Identity.Features.Common;

/// <summary>
///     A refresh token about to be issued: its row id and its plaintext, chosen
///     together and before anything is written.
///     <para>
///         Chosen by the caller rather than inside GenerateRefreshToken, which is why this type
///         exists. Minted inside the retried delegate, a retry after a lost acknowledgement held a
///         different replacement, could not recognise the committed one, found the presented token
///         consumed - which is what reuse looks like - and revoked the family. Chosen outside, the id
///         finds its own rotation and the plaintext is the one that row is the hash of.
///     </para>
/// </summary>
public sealed record IssuedRefreshToken(Guid Id, string Plaintext)
{
    public static IssuedRefreshToken New() => new IssuedRefreshToken(Guid.CreateVersion7(), SecureToken.Generate());
}
