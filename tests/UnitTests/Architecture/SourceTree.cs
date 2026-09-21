using System.Text.RegularExpressions;
namespace UnitTests.Architecture;

/// <summary>
///     Reads the repository for the architecture tests - the checks a compiler cannot make: which
///     schema a module's SQL names, which lives in strings, and whether a name written in prose
///     still resolves to code.
/// </summary>
internal static partial class SourceTree
{
    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    // One alternation so a quote inside a comment, or "//" inside a string, is
    // consumed by whichever token actually starts first.
    [GeneratedRegex(
        "\"\"\"[\\s\\S]*?\"\"\"|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\\\n])*\"|'(?:\\\\.|[^'\\\\\\n])+'|//[^\\n]*|/\\*[\\s\\S]*?\\*/")]
    private static partial Regex CommentOrStringPattern();

    public static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StayStack.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    public static string FindSourceRoot() => Path.Combine(FindRepositoryRoot(), "src");

    /// <summary>Every hand-written .cs file under src, build output and migrations excluded.</summary>
    public static IEnumerable<string> SourceFiles()
    {
        string root = FindSourceRoot();

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return !relative.StartsWith("artifacts/", StringComparison.Ordinal)
                       && !relative.Contains("/bin/", StringComparison.Ordinal)
                       && !relative.Contains("/obj/", StringComparison.Ordinal)
                       && !relative.Contains("/Migrations/", StringComparison.Ordinal);
            });
    }

    /// <summary>
    ///     Comments removed, strings kept - for assertions about SQL a file
    ///     issues, which lives in strings. This codebase explains its locks at
    ///     length, and prose must not satisfy an assertion about code.
    /// </summary>
    public static string WithoutComments(string code) => CommentPattern().Replace(code, string.Empty);

    /// <summary>
    ///     Comments removed and every string or char literal emptied - for
    ///     structural scans, where a call name inside a literal would otherwise
    ///     be read as code.
    /// </summary>
    public static string WithoutCommentsOrStrings(string code) =>
        CommentOrStringPattern().Replace(code, match =>
            match.Value.StartsWith('/') ? string.Empty : match.Value.StartsWith('\'') ? "' '" : "\"\"");

    /// <summary>
    ///     Every string literal in the file, comments excluded - for assertions about SQL text, where
    ///     a type or namespace spelled the same way outside a string must not count as a match.
    /// </summary>
    public static IEnumerable<string> StringLiterals(string code) =>
        CommentOrStringPattern().Matches(code)
            .Select(match => match.Value)
            .Where(value => !value.StartsWith('/'));

    /// <summary>Every hand-written .cs file under tests, build output excluded.</summary>
    public static IEnumerable<string> TestFiles()
    {
        string root = Path.Combine(FindRepositoryRoot(), "tests");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return !relative.Contains("/bin/", StringComparison.Ordinal)
                       && !relative.Contains("/obj/", StringComparison.Ordinal);
            });
    }

    /// <summary>
    ///     The migrations SourceFiles() leaves out. Generated, but named by hand and cited by that
    ///     name in documentation, so a scan of references has to know they exist.
    /// </summary>
    public static IEnumerable<string> MigrationFiles() =>
        Directory.EnumerateFiles(FindSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').Contains("/Migrations/", StringComparison.Ordinal));

    /// <summary>The architecture decision records, README excluded.</summary>
    public static IEnumerable<string> AdrFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), "docs", "adr"), "*.md")
            .Where(path => Path.GetFileName(path) != "README.md");

    /// <summary>
    ///     Each comment line with its 1-based line number - for scans of prose rather than code.
    ///     A "//" inside a string stays out: whichever token starts first consumes the other.
    /// </summary>
    public static IEnumerable<(int Line, string Text)> CommentLines(string code)
    {
        foreach (Match match in CommentOrStringPattern().Matches(code))
        {
            if (!match.Value.StartsWith('/'))
            {
                continue;
            }

            int first = code.AsSpan(0, match.Index).Count('\n') + 1;
            string[] lines = match.Value.Split('\n');

            for (int offset = 0; offset < lines.Length; offset++)
            {
                yield return (first + offset, lines[offset].TrimEnd('\r'));
            }
        }
    }
}
