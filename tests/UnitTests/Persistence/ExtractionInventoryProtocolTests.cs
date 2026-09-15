// PROVES COVERAGE, NOT CORRECTNESS. A green run means docs/extraction-inventory.md names every
// IAtomicScope call site with its owner and participants as written in source - not that the
// participants are the right ones. Verified by adding a participant at one call site: the test fails.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// The extraction inventory is the list of places a module's write commits in another
// module's transaction. A list kept by hand drifts; this test makes a scope the
// inventory does not describe a build failure.
public partial class ExtractionInventoryProtocolTests
{
    [GeneratedRegex(@"\bIAtomicScope\s+(\w+)\b")]
    private static partial Regex AtomicScopeVariable();

    [GeneratedRegex(@"AtomicParticipants\.(\w+)")]
    private static partial Regex Participant();

    // | `File.cs` | Owner | A, B | ...
    [GeneratedRegex(@"^\|\s*`(?<file>[\w.]+\.cs)`\s*\|\s*(?<owner>\w+)\s*\|\s*(?<participants>[\w ,]+?)\s*\|", RegexOptions.Multiline)]
    private static partial Regex InventoryRow();

    private sealed record Scope(string File, string Owner, string Participants);

    private static string Normalise(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));

    private static List<Scope> ScopesInSource()
    {
        List<Scope> scopes = [];

        foreach (string path in SourceTree.SourceFiles())
        {
            string code = SourceTree.WithoutCommentsOrStrings(File.ReadAllText(path));

            foreach (Match variable in AtomicScopeVariable().Matches(code))
            {
                foreach (Match call in Regex.Matches(code, $@"\b{Regex.Escape(variable.Groups[1].Value)}\.ExecuteAsync\s*\("))
                {
                    string arguments = SourceTree.Balanced(code, call.Index + call.Length - 1);

                    // owner, participants, work, token: the first two are the flag
                    // expressions, split at the first top-level comma after the owner.
                    int ownerEnd = arguments.IndexOf(',');
                    string owner = Participant().Match(arguments[..ownerEnd]).Groups[1].Value;
                    string rest = arguments[(ownerEnd + 1)..];
                    string participants = rest[..rest.IndexOf(',')];

                    scopes.Add(new Scope(
                        Path.GetFileName(path),
                        owner,
                        Normalise(Participant().Matches(participants).Select(m => m.Groups[1].Value))));
                }
            }
        }

        return scopes;
    }

    [Fact]
    public void EveryAtomicScopeCallSite_IsInTheInventory_WithItsOwnerAndParticipants()
    {
        List<Scope> source = ScopesInSource();

        // Not vacuous: the scan must find the scopes it guards.
        Assert.True(source.Count >= 9, $"Found only {source.Count} IAtomicScope call sites - the scan is broken.");

        string inventory = File.ReadAllText(Path.Combine(SourceTree.FindRepositoryRoot(), "docs", "extraction-inventory.md"));
        List<Scope> documented = InventoryRow().Matches(inventory)
            .Select(m => new Scope(
                m.Groups["file"].Value,
                m.Groups["owner"].Value,
                Normalise(m.Groups["participants"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))))
            .ToList();

        List<Scope> undocumented = source.Except(documented).ToList();
        List<Scope> stale = documented.Except(source).ToList();

        Assert.True(undocumented.Count == 0 && stale.Count == 0,
            "docs/extraction-inventory.md does not match the IAtomicScope call sites." +
            (undocumented.Count > 0 ? " In source, not in the inventory: " + string.Join("; ", undocumented) + "." : "") +
            (stale.Count > 0 ? " In the inventory, not in source: " + string.Join("; ", stale) + "." : ""));
    }
}
