using System.Text.RegularExpressions;
namespace UnitTests.Architecture;

/// <summary>
///     Reads src for ModuleBoundaryTests, the one check left that a compiler cannot make: which
///     schema a module's SQL names, which lives in strings.
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
}
