// PROVES COVERAGE, NOT CORRECTNESS. A green run means no entity type mints its own identity - not that any caller mints it in the right place; RetryIdentityProtocolTests and the lost-acknowledgement tests are the evidence for that.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// Entities never mint their own identity: every factory takes a caller-supplied
// id, which the creating handler mints on the first line of Handle
// (docs/adr/0025).
//
// A factory that mints is correct only while every caller invokes it outside
// its retry delegate - one moved line from a retry that cannot recognise its
// own row. RetryIdentityProtocolTests can catch such a call only by resolving
// calls transitively from the delegate; this makes the convention checkable by
// direct match.
//
// It does not replace that transitive resolution: identities also reach a
// delegate through same-file helpers and service methods, which a
// delegate-body-only scan misses.
public partial class EntityIdentityProtocolTests
{
    // Keyed by file name. Framework-owned initializers, where the mint is a
    // default the framework path relies on rather than an identity our retry
    // delegates create.
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["ApplicationUser.cs"] =
            "IdentityUser<Guid>.Id defaults here because ASP.NET Identity's own paths construct users. SignUpHandler " +
            "builds its user before the retry and recovers by that id, so no delegate relies on this default.",
        ["RefreshToken.cs"] =
            "A default for the property initializer. Every token this app writes is created by " +
            "AuthTokenProvider.GenerateRefreshToken from a caller-chosen IssuedRefreshToken, which sets Id explicitly."
    };

    [GeneratedRegex(@"\b(?:Guid\.(?:CreateVersion7|NewGuid)|SecureToken\.Generate)\(\)")]
    private static partial Regex Mint();

    [Fact]
    public void NoEntity_MintsItsOwnIdentity()
    {
        List<string> entityFiles = SourceTree.SourceFiles()
            .Where(path => path.Replace('\\', '/').Contains("/Entities/", StringComparison.Ordinal))
            .ToList();

        // Not vacuous: the entities are found, and so are the exempt mints - a
        // scan that saw nothing would pass everything.
        Assert.True(entityFiles.Count >= 15, $"Found only {entityFiles.Count} entity files - the scan is broken.");
        Assert.All(Exempt.Keys, name => Assert.Matches(Mint(),
            SourceTree.WithoutCommentsOrStrings(File.ReadAllText(Assert.Single(entityFiles, f => Path.GetFileName(f) == name))))
        );

        List<string> minting = entityFiles
            .Where(path => !Exempt.ContainsKey(Path.GetFileName(path)))
            .Where(path => Mint().IsMatch(SourceTree.WithoutCommentsOrStrings(File.ReadAllText(path))))
            .Select(Path.GetFileName)
            .ToList()!;

        Assert.True(minting.Count == 0,
            "These entities mint their own identity. Take `Guid id` as the factory's first parameter and let the " +
            "creating handler mint it on the first line of Handle, before anything can retry (docs/adr/0025):\n  " +
            string.Join("\n  ", minting));
    }
}
