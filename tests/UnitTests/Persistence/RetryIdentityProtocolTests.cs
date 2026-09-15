// PROVES COVERAGE, NOT CORRECTNESS. A green run means every participant it can find is present - not that any of them is right; the behavioural tests are the evidence.
// It cannot tell that the recovery lookup exists, runs first, or finds the row, and it follows calls by name, not by type - so a minting method whose name is declared more than once in src is invisible through an instance call.
// Evidence that recovery works: the lost-acknowledgement tests in CreationAmbiguityTests, CatalogRetryTests and PromotionRedemptionRetryTests.

using System.Text.RegularExpressions;
namespace UnitTests.Persistence;

// A change-detector for docs/adr/0025's identity rule: an identity a retry needs
// to recognise its own committed write is generated OUTSIDE the retried
// delegate. An identity, not a Guid - RefreshTokenHandler's replacement token is
// a string from SecureToken.Generate.
//
// EnableRetryOnFailure cannot tell a failed commit from one that landed and lost
// its acknowledgement, so it re-runs the delegate either way. A delegate that
// mints its identity inside gets a different one on the second attempt, cannot
// find the row the first attempt wrote, and reports that row as somebody else's
// conflict.
//
// What counts as minting: Guid.CreateVersion7(), Guid.NewGuid() or
// SecureToken.Generate() written in the delegate or in a same-file method it
// calls (followed transitively); a call to a static factory anywhere in src that
// mints; an instance call to a method whose name is declared once in src and
// mints (through an interface, from another file); or `new T` where T mints in a
// field or property initializer or its constructor.
//
// Entity factories do not mint (EntityIdentityProtocolTests checks that by
// direct match), but calls must still be followed: identities also reach a
// delegate through same-file helpers and service methods, which a scan of
// delegate bodies alone misses.
//
// Crude by design - a regex over brace-matched bodies - so it carries an
// allow-list. Every entry is a deliberate decision with a reason attached.
public partial class RetryIdentityProtocolTests
{
    // Keyed by what mints, not by where: a factory whose ids are never used for
    // recovery is safe in every delegate, so listing it once is the honest shape.
    private static readonly Dictionary<string, string> AllowedMinting = new()
    {
        [".GenerateJwtToken"] =
            "Mints the access token's jti claim. Access tokens are stateless and never stored or looked up, so a " +
            "retry that signs a different one recovers nothing by it - both are equally valid for the same user."
    };

    [GeneratedRegex(@"\bIExecutionStrategy\s+(\w+)\s*=")]
    private static partial Regex StrategyVariable();

    [GeneratedRegex(@"\b(?:Guid\.(?:CreateVersion7|NewGuid)|SecureToken\.Generate)\(\)")]
    private static partial Regex DirectMint();

    // A method declaration: modifiers, a return type, a name, a parameter list,
    // then a body or an expression body. Deliberately loose.
    [GeneratedRegex(
        @"(?:\b(?:public|private|protected|internal)\b[\w\s<>\[\],?.()]*?)\b(\w+)\s*(?:<[^>(]*>)?\s*\((?=[^;{}]*\)\s*(?:\{|=>))")]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(\w+)")]
    private static partial Regex TypeDeclaration();

    private sealed record Method(string Type, string Name, string Body, int Start, int End);

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
            string body = BodyAfter(code, parameters);
            int start = code.IndexOf(body, parameters, StringComparison.Ordinal);
            methods.Add(new Method(type, match.Groups[1].Value, body, start, start + body.Length));
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

    // Method names declared exactly once in src whose body mints - safe to follow
    // through an instance call on any receiver, because the name can only mean
    // that one method.
    private static HashSet<string> UniquelyNamedMinters(IEnumerable<(string Path, string Code)> sources)
    {
        List<Method> all = sources.SelectMany(source => MethodsIn(source.Code)).ToList();

        return all.GroupBy(method => method.Name)
            .Where(group => group.Count() == 1 && DirectMint().IsMatch(group.Single().Body))
            .Select(group => group.Key)
            .ToHashSet();
    }

    // Types that mint when constructed: a mint in a field or property initializer
    // (outside every method body), or in a constructor.
    private static HashSet<string> MintingTypes(IEnumerable<(string Path, string Code)> sources)
    {
        HashSet<string> types = [];

        foreach ((string _, string code) in sources)
        {
            List<Method> methods = MethodsIn(code);
            List<(int Index, string Name)> declared = TypeDeclaration().Matches(code)
                .Select(m => (m.Index, m.Groups[1].Value)).ToList();

            foreach (Match mint in DirectMint().Matches(code))
            {
                Method? enclosing = methods.FirstOrDefault(m => m.Start <= mint.Index && mint.Index < m.End);

                if (enclosing is null || enclosing.Name == enclosing.Type)
                {
                    string? type = declared.LastOrDefault(t => t.Index < mint.Index).Name;

                    if (type is not null)
                    {
                        types.Add(type);
                    }
                }
            }
        }

        return types;
    }

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

        foreach (string name in _uniqueMinters)
        {
            if (Regex.IsMatch(body, $@"\.{Regex.Escape(name)}\s*\("))
            {
                yield return $".{name}(...)";
            }
        }

        foreach (string type in _mintingTypes)
        {
            if (Regex.IsMatch(body, $@"\bnew\s+(?:[\w.]+\.)?{Regex.Escape(type)}\s*[({{]"))
            {
                yield return $"new {type}";
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

    private static HashSet<string> _uniqueMinters = [];
    private static HashSet<string> _mintingTypes = [];

    [Fact]
    public void NoRetriedDelegate_MintsAnIdentity_OutsideTheAllowList()
    {
        List<(string Path, string Code)> sources = LoadSources().Values.ToList();
        HashSet<string> factories = MintingFactories(sources);
        _uniqueMinters = UniquelyNamedMinters(sources);
        _mintingTypes = MintingTypes(sources);
        List<(string File, string Body)> delegates = RetryDelegates(sources);

        // Not vacuous: the scan must find retry delegates, and must recognise a
        // factory that mints. Entity factories do not (EntityIdentityProtocolTests),
        // so the anchor is IssuedRefreshToken.New, which mints by design and is
        // exactly the kind of call that must stay outside a delegate.
        Assert.True(delegates.Count >= 15, $"Found only {delegates.Count} retry delegates - the scan is broken.");
        Assert.True(factories.Contains("IssuedRefreshToken.New"),
            "The scan no longer recognises IssuedRefreshToken.New as minting: " + string.Join(", ", factories));

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
