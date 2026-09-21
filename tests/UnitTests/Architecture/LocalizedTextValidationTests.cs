using System.Text.RegularExpressions;
namespace UnitTests.Architecture;

// A localized name arrives as a free-form Dictionary<string, string>, and what may be in it is
// decided by one rule (BuildingBlocks.LocalizedTextRules). Six endpoints took one before anything
// checked it: a payload without the default culture reached LocalizedText.Create, which throws
// ArgumentException, which GlobalExceptionHandler deliberately has no arm for - so a fixable
// payload came back as a 500, and nothing bounded the keys or the lengths at all.
//
// The seventh endpoint is the one this exists for. A source scan rather than reflection over request
// types: these run without a host, and a reflective walk of the assembly is the kind of thing
// docs/adr/0001 keeps out of the build.
public partial class LocalizedTextValidationTests
{
    [GeneratedRegex(@"Dictionary<string, string>\??\s+(\w+)\s*\{")]
    private static partial Regex LocalizedProperty();

    [Fact]
    public void EveryRequestTakingALocalizedDictionary_ValidatesItWithTheSharedRule()
    {
        List<string> violations = [];
        int checked_ = 0;

        foreach (string request in SourceTree.SourceFiles().Where(path => path.EndsWith("Request.cs", StringComparison.Ordinal)))
        {
            string code = SourceTree.WithoutCommentsOrStrings(SourceTree.Read(request));
            MatchCollection localized = LocalizedProperty().Matches(code);

            if (localized.Count == 0)
            {
                continue;
            }

            checked_++;
            string validator = request[..^"Request.cs".Length] + "RequestValidator.cs";

            if (!File.Exists(validator))
            {
                violations.Add($"{Relative(request)} has a localized dictionary and no validator beside it");
                continue;
            }

            string rules = SourceTree.Read(validator);

            foreach (Match property in localized)
            {
                string name = property.Groups[1].Value;

                // The rule has to be applied to that property, not merely present in the file: a
                // request with two localized fields can easily validate one of them.
                if (!Regex.IsMatch(rules, $@"RuleFor\(\s*\w+\s*=>\s*\w+\.{Regex.Escape(name)}\s*\)[\s\S]{{0,400}}?\.LocalizedText\("))
                {
                    violations.Add($"{Relative(request)}: {name} is not validated with LocalizedText(...)");
                }
            }
        }

        Assert.True(checked_ > 3, $"Only {checked_} requests with a localized dictionary were found, so this scan is not reading the tree.");
        Assert.True(violations.Count == 0,
            "A request takes a localized dictionary that nothing checks. Apply " +
            "LocalizedTextRules.LocalizedText(localization, maxLength) in its validator - unchecked, a " +
            "payload without the default culture reaches LocalizedText.Create and leaves as a 500:\n  " +
            string.Join("\n  ", violations));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(SourceTree.FindRepositoryRoot(), path).Replace('\\', '/');
}
