using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// A change-detector for docs/adr/0025's identity rule: an id a retry needs to
// recognise its own committed write is generated OUTSIDE the retried delegate.
//
// EnableRetryOnFailure cannot tell a failed commit from one that landed and lost
// its acknowledgement, so it re-runs the delegate either way. A delegate that
// mints its identity inside gets a different one on the second attempt, can no
// longer find the row the first attempt wrote, and reports that row as somebody
// else's conflict. CancelBookingHandler, DeleteUnitHandler,
// DeletePropertyHandler, UpdatePricingRuleHandler, InitiateTransactionHandler
// and CreatePricingRuleHandler each broke this rule, and each was found by
// grepping by hand - which is how the last one was missed.
//
// What counts as minting: Guid.CreateVersion7() or Guid.NewGuid() written in the
// delegate, in a method of the same file the delegate calls (followed
// transitively - InitiateTransactionHandler minted two calls deep), or a call to
// a factory anywhere in src that mints one itself. The last is how both of the
// most recent cases hid: Transaction.Create and PricingRule.Create* generated
// their own ids.
//
// Crude by design - a regex over brace-matched bodies - so it carries an
// allow-list. Every entry is a deliberate decision with a reason attached, which
// is exactly what the violations never had.
public partial class RetryIdentityProtocolTests
{
    // Keyed by what mints, not by where: a factory whose ids are never used for
    // recovery is safe in every delegate, so listing it once is the honest shape.
    private static readonly Dictionary<string, string> AllowedMinting = new()
    {
    };

    [GeneratedRegex(@"\bIExecutionStrategy\s+(\w+)\s*=")]
    private static partial Regex StrategyVariable();

    [GeneratedRegex(@"\bGuid\.(CreateVersion7|NewGuid)\(\)")]
    private static partial Regex DirectMint();

    // A method declaration: modifiers, a return type, a name, a parameter list,
    // then a body or an expression body. Deliberately loose.
    [GeneratedRegex(
        @"(?:\b(?:public|private|protected|internal)\b[\w\s<>\[\],?.()]*?)\b(\w+)\s*(?:<[^>(]*>)?\s*\((?=[^;{}]*\)\s*(?:\{|=>))")]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(\w+)")]
    private static partial Regex TypeDeclaration();

    private sealed record Method(string Type, string Name, string Body);

    private static string BodyAfter(string code, int parameterListStart)
    {
        int afterParameters = parameterListStart + SourceTree.Balanced(code, parameterListStart).Length;
        int brace = code.IndexOf('{', afterParameters);
        int arrow = code.IndexOf("=>", afterParameters, StringComparison.Ordinal);

        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            int end = code.IndexOf(';', arrow);
            return end < 0 ? code[arrow..] : code[arrow..end];
        }

        return brace < 0 ? string.Empty : SourceTree.Balanced(code, brace);
    }

    private static List<Method> MethodsIn(string code)
    {
        // The nearest preceding type declaration names the method's type -
        // enough for top-level entity and handler files, which is all this needs.
        List<(int Index, string Name)> types = TypeDeclaration().Matches(code)
            .Select(m => (m.Index, m.Groups[1].Value)).ToList();

        List<Method> methods = [];

        foreach (Match match in MethodDeclaration().Matches(code))
        {
            int parameters = code.IndexOf('(', match.Groups[1].Index + match.Groups[1].Length);
            string type = types.LastOrDefault(t => t.Index < match.Index).Name ?? string.Empty;
            methods.Add(new Method(type, match.Groups[1].Value, BodyAfter(code, parameters)));
        }

        return methods;
    }

    private static Dictionary<string, (string Path, string Code)> LoadSources() =>
        SourceTree.SourceFiles().ToDictionary(
            path => path,
            path => (path, SourceTree.WithoutCommentsOrStrings(File.ReadAllText(path))));

    // Type.Method for every static-looking factory in src that mints an id in
    // its own body.
    private static HashSet<string> MintingFactories(IEnumerable<(string Path, string Code)> sources) =>
        sources
            .SelectMany(source => MethodsIn(source.Code))
            .Where(method => method.Type.Length > 0 && DirectMint().IsMatch(method.Body))
            .Select(method => $"{method.Type}.{method.Name}")
            .ToHashSet();

    private static IEnumerable<string> MintingIn(
        string body, List<Method> sameFile, HashSet<string> factories, HashSet<string> visited)
    {
        foreach (Match mint in DirectMint().Matches(body))
        {
            yield return mint.Value;
        }

        foreach (string factory in factories)
        {
            if (Regex.IsMatch(body, $@"\b{Regex.Escape(factory)}\s*\("))
            {
                yield return factory + "(...)";
            }
        }

        foreach (Method helper in sameFile)
        {
            if (!visited.Contains(helper.Name)
                && Regex.IsMatch(body, $@"(?<![\w.])(?:this\.)?{Regex.Escape(helper.Name)}\s*[<(]"))
            {
                visited.Add(helper.Name);

                foreach (string inner in MintingIn(helper.Body, sameFile, factories, visited))
                {
                    yield return $"{inner} via {helper.Name}";
                }
            }
        }
    }

    private static List<(string File, string Body)> RetryDelegates(IEnumerable<(string Path, string Code)> sources)
    {
        List<(string, string)> delegates = [];

        foreach ((string path, string code) in sources)
        {
            foreach (Match variable in StrategyVariable().Matches(code))
            {
                string call = $@"\b{Regex.Escape(variable.Groups[1].Value)}\.ExecuteAsync\s*\(";

                foreach (Match execute in Regex.Matches(code, call))
                {
                    int open = execute.Index + execute.Length - 1;
                    delegates.Add((path, SourceTree.Balanced(code, open)));
                }
            }
        }

        return delegates;
    }

    [Fact]
    public void NoRetriedDelegate_MintsAnIdentity_OutsideTheAllowList()
    {
        List<(string Path, string Code)> sources = LoadSources().Values.ToList();
        HashSet<string> factories = MintingFactories(sources);
        List<(string File, string Body)> delegates = RetryDelegates(sources);

        // Not vacuous: the scan must find retry delegates, and must recognise a
        // factory that mints its own id when one exists - Unit.Create does.
        Assert.True(delegates.Count >= 15, $"Found only {delegates.Count} retry delegates - the scan is broken.");
        Assert.True(factories.Contains("Unit.Create"),
            "The scan no longer recognises Unit.Create as minting its own id: " + string.Join(", ", factories));

        List<string> violations = [];

        foreach ((string file, string body) in delegates)
        {
            List<Method> sameFile = MethodsIn(sources.Single(s => s.Path == file).Code);

            foreach (string minting in MintingIn(body, sameFile, factories, [])
                         .Where(m => !AllowedMinting.Keys.Any(allowed =>
                             m.StartsWith(allowed, StringComparison.Ordinal))))
            {
                violations.Add($"{Path.GetFileName(file)}: {minting}");
            }
        }

        Assert.True(violations.Count == 0,
            "A retried delegate mints an identity, so a retry after a lost acknowledgement cannot recognise " +
            "its own committed write (docs/adr/0025). Generate the id outside strategy.ExecuteAsync and pass " +
            "it in - or, if recovery genuinely never needs it, add it to AllowedMinting with the reason:\n  " +
            string.Join("\n  ", violations.Distinct()));
    }
}
