using System.Text.RegularExpressions;
namespace UnitTests.Architecture;

// A document that names a type, a file or an ADR makes a claim no compiler checks. A name that has
// stopped resolving is worse than no name: it sends a reader looking for code that is not there, and
// it reads as a guarantee the system no longer makes. These scans read docs/adr and every comment
// under src and tests, and fail on a name the repository cannot produce.
//
// Deliberate prose - an external product, a placeholder, a framework type that happens to share one
// of this codebase's suffixes - belongs in DocumentationReferenceAllowList.txt, with the reason
// written in the entry. Widening a pattern until the run is green is the one repair that is never
// right: it removes the check instead of the stale name.
//
// Structural, not behavioural: green means every name these scans can see resolves to something.
// Prose that is merely wrong about what the code does still passes.
public partial class DocumentationReferenceTests
{
    // The suffixes this codebase names types with. A backticked word ending in one is a reference to
    // code rather than prose, so it has to resolve.
    private static readonly string[] TypeSuffixes =
    [
        "Analyzer", "Constraint", "Context", "Endpoint", "Handler", "Interceptor", "Job", "Lock",
        "Model", "Registration", "Runner", "Tests", "Validator"
    ];

    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\bnamespace\s+([A-Za-z_][\w.]*)")]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifier();

    // A using directive or a namespace declaration names things the compiler resolves elsewhere, and
    // a folder-deep namespace segment is not evidence that a module by that name exists.
    [GeneratedRegex(@"^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?[A-Za-z_][\w.]*[ \t]*;[ \t]*$|^[ \t]*namespace[ \t]+[\w.]+[ \t]*[;{]?[ \t]*$",
        RegexOptions.Multiline)]
    private static partial Regex NamespaceOrUsingLine();

    [GeneratedRegex(@"docs/adr/(\d{4})")]
    private static partial Regex AdrPathReference();

    [GeneratedRegex(@"\]\((\d{4}-[A-Za-z0-9-]+\.md)\)")]
    private static partial Regex AdrLinkReference();

    [GeneratedRegex("`([A-Za-z][A-Za-z0-9]*)`")]
    private static partial Regex BacktickedWord();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)+\b")]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"^\|\s*\[(\d{4})\]\(([^)]+)\)\s*\|")]
    private static partial Regex IndexRow();

    private static readonly string RepositoryRoot = SourceTree.FindRepositoryRoot();

    private static readonly string AdrDirectory = Path.Combine(RepositoryRoot, "docs", "adr");

    private static readonly IReadOnlyList<string> CodeFiles =
        [.. SourceTree.SourceFiles(), .. SourceTree.TestFiles()];

    private static readonly IReadOnlyDictionary<string, string> Code =
        CodeFiles.ToDictionary(path => path, path => SourceTree.WithoutCommentsOrStrings(File.ReadAllText(path)));

    private static readonly HashSet<string> DeclaredNames = BuildDeclaredNames();

    private static readonly HashSet<string> QualifierRoots = BuildQualifierRoots();

    private static readonly IReadOnlyDictionary<string, string> AllowList =
        ReadAnnotatedList("DocumentationReferenceAllowList.txt");

    private static readonly IReadOnlyDictionary<string, string> RetiredNames =
        ReadAnnotatedList("RetiredNames.txt");

    private static readonly IReadOnlyList<string> AdrFileNames =
        [.. SourceTree.AdrFiles().Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>Every type this repository declares, plus every file and migration it can be cited by.</summary>
    private static HashSet<string> BuildDeclaredNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach ((string path, string code) in Code)
        {
            names.Add(Path.GetFileNameWithoutExtension(path));

            foreach (Match declaration in TypeDeclaration().Matches(code))
            {
                names.Add(declaration.Groups[1].Value);
            }
        }

        // A migration is cited by the name it was created with, which is the file name after its timestamp.
        foreach (string path in SourceTree.MigrationFiles())
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int timestamp = name.IndexOf('_', StringComparison.Ordinal);
            names.Add(timestamp < 0 ? name : name[(timestamp + 1)..]);
        }

        return names;
    }

    /// <summary>
    ///     What may stand to the left of a dot: a declared name, a project, a namespace root, or any
    ///     identifier the code itself names. A framework type a comment discusses is nearly always one
    ///     the code also uses; one that appears nowhere is either prose or a name that has gone.
    /// </summary>
    private static HashSet<string> BuildQualifierRoots()
    {
        HashSet<string> roots = new(DeclaredNames, StringComparer.Ordinal);

        string[] projectRoots = [SourceTree.FindSourceRoot(), Path.Combine(RepositoryRoot, "tests")];

        foreach (string path in projectRoots.SelectMany(root =>
                     Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            roots.Add(name);
            roots.UnionWith(name.Split('.'));
        }

        // A top-level folder under src roots a namespace the same way a project does.
        foreach (string directory in Directory.EnumerateDirectories(SourceTree.FindSourceRoot()))
        {
            roots.Add(Path.GetFileName(directory));
        }

        foreach (string code in Code.Values)
        {
            foreach (Match declaration in NamespaceDeclaration().Matches(code))
            {
                roots.Add(declaration.Groups[1].Value.Split('.')[0]);
            }

            foreach (Match identifier in Identifier().Matches(NamespaceOrUsingLine().Replace(code, string.Empty)))
            {
                roots.Add(identifier.Value);
            }
        }

        return roots;
    }

    /// <summary>One "name; why" entry per line, blank lines and # comments skipped.</summary>
    private static IReadOnlyDictionary<string, string> ReadAnnotatedList(string fileName)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);

        foreach (string line in File.ReadAllLines(Path.Combine(RepositoryRoot, "tests", "UnitTests", "Architecture", fileName)))
        {
            string entry = line.Trim();

            if (entry.Length == 0 || entry.StartsWith('#'))
            {
                continue;
            }

            int separator = entry.IndexOf(';', StringComparison.Ordinal);
            entries[separator < 0 ? entry : entry[..separator].Trim()] =
                separator < 0 ? string.Empty : entry[(separator + 1)..].Trim();
        }

        return entries;
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    private static IEnumerable<(string Where, int Line, string Text)> AdrLines()
    {
        foreach (string path in SourceTree.AdrFiles())
        {
            string where = Relative(path);
            string[] lines = File.ReadAllLines(path);

            for (int index = 0; index < lines.Length; index++)
            {
                yield return (where, index + 1, lines[index]);
            }
        }
    }

    private static IEnumerable<(string Where, int Line, string Text)> CommentLines()
    {
        foreach (string path in CodeFiles)
        {
            string where = Relative(path);

            foreach ((int line, string text) in SourceTree.CommentLines(File.ReadAllText(path)))
            {
                yield return (where, line, text);
            }
        }
    }

    [Fact]
    public void EveryAdrReferenceNamesAnAdrThatExists()
    {
        HashSet<string> numbers = [.. AdrFileNames.Select(name => name[..4])];
        List<string> violations = [];
        int references = 0;

        foreach ((string where, int line, string text) in AdrLines().Concat(CommentLines()))
        {
            foreach (Match reference in AdrPathReference().Matches(text))
            {
                references++;

                if (!numbers.Contains(reference.Groups[1].Value))
                {
                    violations.Add($"{where}:{line}: {reference.Value} - docs/adr has no {reference.Groups[1].Value}");
                }
            }

            foreach (Match link in AdrLinkReference().Matches(text))
            {
                references++;

                if (!AdrFileNames.Contains(link.Groups[1].Value))
                {
                    violations.Add($"{where}:{line}: {link.Groups[1].Value} - no such file in docs/adr");
                }
            }
        }

        Assert.True(references > 20, $"Only {references} ADR references were found, so this scan is not reading the tree.");
        Assert.True(violations.Count == 0,
            "A comment or an ADR points at an ADR that is not there. Point it at the decision that " +
            "replaced it, or drop the reference:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryTypeNameAnAdrCitesResolves()
    {
        List<string> violations = [];
        int cited = 0;

        foreach ((string where, int line, string text) in AdrLines())
        {
            foreach (Match backticked in BacktickedWord().Matches(text))
            {
                string name = backticked.Groups[1].Value;

                if (!char.IsUpper(name[0]) || !TypeSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
                {
                    continue;
                }

                cited++;

                if (!DeclaredNames.Contains(name) && !AllowList.ContainsKey(name))
                {
                    violations.Add($"{where}:{line}: `{name}`");
                }
            }
        }

        Assert.True(cited > 50, $"Only {cited} type names were found in the ADRs, so this scan is not reading them.");
        Assert.True(violations.Count == 0,
            "An ADR names a type that no longer exists. Name what enforces the decision today. If the " +
            "word is prose or a framework type, add it to DocumentationReferenceAllowList.txt with the " +
            "reason - never by loosening the pattern:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryQualifiedNameInProseHasARootThatResolves()
    {
        List<string> violations = [];
        int qualified = 0;

        foreach ((string where, int line, string text) in AdrLines().Concat(CommentLines()))
        {
            foreach (Match name in QualifiedName().Matches(text))
            {
                qualified++;
                string root = name.Value.Split('.')[0];

                if (!QualifierRoots.Contains(root) && !AllowList.ContainsKey(name.Value))
                {
                    violations.Add($"{where}:{line}: {name.Value} - nothing named {root} is in the tree");
                }
            }
        }

        Assert.True(qualified > 100, $"Only {qualified} qualified names were found, so this scan is not reading the tree.");
        Assert.True(violations.Count == 0,
            "A comment or an ADR qualifies a name with a module, namespace or type that no longer " +
            "exists. Use the name the code has now, or allow-list the entry with its reason:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void NoCommentNamesSomethingTheRepositoryRetired()
    {
        // Comments only. An ADR names what was rejected or retired on purpose - that is its job.
        List<string> violations = [];

        foreach ((string where, int line, string text) in CommentLines())
        {
            foreach ((string retired, string replacement) in RetiredNames)
            {
                // Not adjacent to a path separator or an identifier character: Endpoints/Availability is
                // a folder that exists, and UnitAvailabilityLookup is a type that does.
                if (Regex.IsMatch(text, $@"(?<![A-Za-z0-9_/]){Regex.Escape(retired)}(?![A-Za-z0-9_/])"))
                {
                    violations.Add($"{where}:{line}: {retired} - {replacement}");
                }
            }
        }

        Assert.True(RetiredNames.Count > 0, "The retired-name list is empty, so this scan proves nothing.");
        Assert.True(violations.Count == 0,
            "A comment describes something this repository no longer has. Rewrite the comment for the " +
            "system that exists:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void TheIndexAndTheDirectoryAreTheSameSet()
    {
        Dictionary<string, string> indexed = new(StringComparer.Ordinal);

        foreach (string line in File.ReadAllLines(Path.Combine(AdrDirectory, "README.md")))
        {
            Match row = IndexRow().Match(line);

            if (row.Success)
            {
                indexed[row.Groups[1].Value] = row.Groups[2].Value;
            }
        }

        List<string> violations = [];

        foreach (string fileName in AdrFileNames)
        {
            if (!indexed.TryGetValue(fileName[..4], out string? linked))
            {
                violations.Add($"{fileName} is in docs/adr but has no row in the index");
            }
            else if (!string.Equals(linked, fileName, StringComparison.Ordinal))
            {
                violations.Add($"the index row for {fileName[..4]} links {linked}, but the file is {fileName}");
            }
        }

        foreach ((string number, string linked) in indexed)
        {
            if (!AdrFileNames.Contains(linked))
            {
                violations.Add($"the index lists {number} ({linked}), which is not in docs/adr");
            }
        }

        Assert.True(indexed.Count > 0, "No index rows parsed, so this check proves nothing.");
        Assert.True(violations.Count == 0,
            "docs/adr/README.md and docs/adr disagree about which decisions exist:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryAllowListEntryCarriesItsReason()
    {
        List<string> unexplained =
        [
            .. AllowList.Concat(RetiredNames).Where(entry => entry.Value.Length == 0).Select(entry => entry.Key)
        ];

        Assert.True(unexplained.Count == 0,
            "An entry exempts a name without saying why, which is how an allow-list becomes the rule:\n  " +
            string.Join("\n  ", unexplained));
    }
}
