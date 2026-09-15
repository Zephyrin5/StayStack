// PROVES COVERAGE, NOT CORRECTNESS. A green run means no source file registers a pooled DbContext - not that every context is safe to enlist in an atomic scope.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// IAtomicScope lends a borrower the owner's connection and transaction, then gives
// it back. A pooled context returned to the pool mid-lend, or with a lingering
// enlistment, would carry both into an unrelated request. Every module context is
// registered with AddDbContext; this keeps it that way.
public partial class DbContextRegistrationProtocolTests
{
    [GeneratedRegex(@"\b(?:AddDbContextPool|AddPooledDbContextFactory)\b")]
    private static partial Regex PooledRegistration();

    [GeneratedRegex(@"\bAddAtomicParticipant<")]
    private static partial Regex ParticipantRegistration();

    [Fact]
    public void NoContextIsPooled_AndParticipantsAreRegistered()
    {
        List<string> files = SourceTree.SourceFiles().ToList();
        List<string> sources = files.Select(f => SourceTree.WithoutCommentsOrStrings(File.ReadAllText(f))).ToList();

        // Not vacuous: the scan must see the participant registrations it protects.
        Assert.True(sources.Count(s => ParticipantRegistration().IsMatch(s)) >= 7,
            "Fewer than seven AddAtomicParticipant registrations found - the scan is broken.");

        List<string> pooled = files.Where((_, i) => PooledRegistration().IsMatch(sources[i])).Select(Path.GetFileName).ToList()!;
        Assert.True(pooled.Count == 0,
            "A pooled DbContext registration cannot take part in IAtomicScope: " + string.Join(", ", pooled));
    }
}
