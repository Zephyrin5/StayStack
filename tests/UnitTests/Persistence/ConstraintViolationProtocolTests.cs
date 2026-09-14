// PROVES COVERAGE, NOT CORRECTNESS. A green run means no code outside ConstraintViolations inspects an integrity violation - not that any catch names the right constraint; each handler's behavioural test is the evidence for that.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// Constraint violations are matched by constraint name, through
// Persistence.ConstraintViolations. A catch on SQLSTATE alone gives the same answer
// to constraints that mean different things - a primary key colliding with this
// operation's own committed row, and a unique index held by another row - so a
// recoverable duplicate reads as a conflict.
//
// This makes the SQLSTATE-only form unexpressible outside that one file: any other
// file in src naming an integrity-violation code, a class-23 SQLSTATE literal, or
// PostgresException.ConstraintName fails here.
public partial class ConstraintViolationProtocolTests
{
    private const string HelperFile = "ConstraintViolations.cs";

    [GeneratedRegex(@"PostgresErrorCodes\.\w*Violation\b|\.ConstraintName\b|""23[0-9A-Z]{3}""")]
    private static partial Regex IntegrityInspection();

    [Fact]
    public void OnlyConstraintViolations_InspectsAnIntegrityViolation()
    {
        List<string> files = SourceTree.SourceFiles().ToList();

        // Not vacuous: the helper is found, and the scan recognises its inspection.
        string helper = Assert.Single(files, f => Path.GetFileName(f) == HelperFile);
        Assert.Matches(IntegrityInspection(), SourceTree.WithoutComments(File.ReadAllText(helper)));

        List<string> offenders = files
            .Where(f => f != helper)
            .Where(f => IntegrityInspection().IsMatch(SourceTree.WithoutComments(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToList()!;

        Assert.True(offenders.Count == 0,
            "These files inspect a constraint violation directly. Match it by name through " +
            "Persistence.ConstraintViolations (IsViolationOf, IsViolationOfAny, IsPrimaryKeyViolationOf), taking the " +
            "name from the EF model or a constant the configuration shares:\n  " + string.Join("\n  ", offenders));
    }
}
