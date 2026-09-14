// PROVES COVERAGE, NOT CORRECTNESS. A green run means no integration test reaches for a commit or command hook directly - not that the faults injected through CommitFaults are placed or targeted well; each test's HasFired assertion and its mutation probe are the evidence.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// Six tests in the integration suite ended up with their failure injected on the
// wrong side of a commit - most silently, when an explicit transaction was added
// around the code under test and a hook that used to follow the commit began to
// precede it. At six this was a tooling problem, not carelessness.
//
// CommitFaults now offers exactly two entry points, FailAfterCommit and
// FailBeforeCommit, and owns the hook each maps to. This keeps it that way: an
// integration test that names a transaction or command interceptor - or
// substitutes the audit interceptor, which is how the old ones got their seam -
// fails here, and is pointed at the helper whose method name states its intent.
public partial class FaultInjectionProtocolTests
{
    // Always a fault seam: nothing else in a test has a reason to hook a commit,
    // and substituting the audit interceptor is how the old faults were attached.
    [GeneratedRegex(
        @"\b(?:I?DbTransactionInterceptor|TransactionCommitt(?:ed|ing)Async|AddScoped<AuditableEntitySaveChangesInterceptor)\b")]
    private static partial Regex DirectHook();

    // A command hook is also how round trips get counted (PagedSliceTests), so it
    // is only a fault seam in a file that also throws a database exception - the
    // shape the transactions lost-ack test had before it moved to the commit.
    [GeneratedRegex(@"\b(?:I?DbCommandInterceptor|(?:Reader|NonQuery|Scalar)Execut(?:ed|ing)Async)\b")]
    private static partial Regex CommandHook();

    [GeneratedRegex(@"\bthrow\s+new\s+(?:Postgres|Npgsql|DbUpdate)\w*Exception\b")]
    private static partial Regex ThrowsADatabaseException();

    private static bool InjectsDirectly(string code) =>
        DirectHook().IsMatch(code) || (CommandHook().IsMatch(code) && ThrowsADatabaseException().IsMatch(code));

    [Fact]
    public void NoIntegrationTest_InjectsACommitFault_ExceptThroughCommitFaults()
    {
        List<string> files = SourceTree.IntegrationTestFiles().ToList();

        // Not vacuous: the helper itself must be found, and must be the one file
        // allowed to name the hooks.
        string helper = Assert.Single(files, f => Path.GetFileName(f) == "CommitFaults.cs");
        Assert.True(InjectsDirectly(SourceTree.WithoutCommentsOrStrings(File.ReadAllText(helper))),
            "The scan no longer recognises CommitFaults' own hooks - it would recognise nobody else's either.");

        List<string> offenders = files
            .Where(f => f != helper)
            .Where(f => InjectsDirectly(SourceTree.WithoutCommentsOrStrings(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToList()!;

        Assert.True(offenders.Count == 0,
            "These integration tests name a commit or command hook directly. Use CommitFaults.FailAfterCommit " +
            "(a lost acknowledgement) or CommitFaults.FailBeforeCommit (a failed commit), so the side of the " +
            "commit a fault lands on is stated rather than implied:\n  " + string.Join("\n  ", offenders));
    }
}
