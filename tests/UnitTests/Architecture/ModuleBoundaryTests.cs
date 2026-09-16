using System.Text.RegularExpressions;
using UnitTests.Persistence;
namespace UnitTests.Architecture;

// The module boundary used to be enforced by each module owning a DbContext: a module could not write
// another's tables because it could not see them. One AppDbContext removes that, so the boundary now
// rests on three things the compiler does not check on its own - the project graph, who may hold the
// context, and which schema a module's SQL names. These check all three by reading the repository.
//
// Structural, not behavioural: a green run means nothing crosses a boundary in a way this scan can
// see. Raw SQL assembled at runtime, or reflection, would not be visible here.
public partial class ModuleBoundaryTests
{
    // Downstream of raw inventory, in order. A module may reference an earlier module's Contracts,
    // never a later one's (docs/adr/0004).
    private static readonly string[] Order = ["Hosts", "Catalog", "Promotions", "Bookings", "Transactions"];

    private static readonly string[] Modules =
        ["Bookings", "Catalog", "Hosts", "Identity", "Promotions", "Reviews", "Transactions"];

    // Identity is outside the order: it links a user to a host and knows nothing else.
    private const string Identity = "Identity";

    // Reviews consumes and exposes nothing, so it has no Contracts project to reference.
    private const string Reviews = "Reviews";

    [GeneratedRegex(@"ProjectReference Include=""([^""]+)""")]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"\b(\w+)Model\.Schema\b")]
    private static partial Regex SchemaConstant();

    private static string ModuleDirectory(string module) =>
        Path.Combine(SourceTree.FindSourceRoot(), "Modules", module);

    private static IReadOnlyList<string> ReferencesOf(string csproj) =>
        ProjectReference().Matches(File.ReadAllText(csproj))
            .Select(match => Path.GetFileNameWithoutExtension(match.Groups[1].Value.Replace('\\', '/')))
            .ToList();

    private static string MainProjectOf(string module) =>
        Path.Combine(ModuleDirectory(module), $"{module}.csproj");

    [Fact]
    public void NoModuleReferencesAnotherModulesMainProject()
    {
        List<string> violations = [];

        foreach (string module in Modules)
        {
            foreach (string reference in ReferencesOf(MainProjectOf(module)))
            {
                if (Modules.Contains(reference) && reference != module)
                {
                    violations.Add($"{module} references {reference}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "A module references another module's main project, which puts its entities and handlers in " +
            "reach. Modules meet through Contracts projects only (docs/adr/0004):\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryContractsReferenceRunsUpstream()
    {
        // Not vacuous: Bookings genuinely depends on three upstream Contracts projects.
        Assert.Contains("Catalog.Contracts", ReferencesOf(MainProjectOf("Bookings")));

        List<string> violations = [];

        foreach (string module in Modules)
        {
            foreach (string reference in ReferencesOf(MainProjectOf(module)))
            {
                if (!reference.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    continue;
                }

                string referenced = reference[..^".Contracts".Length];

                if (referenced == module)
                {
                    continue;
                }

                if (referenced == Reviews)
                {
                    violations.Add($"{module} references {reference}: nothing may depend on Reviews");
                    continue;
                }

                if (module == Identity)
                {
                    if (referenced != "Hosts")
                    {
                        violations.Add($"Identity references {reference}: Identity may reference only Hosts.Contracts");
                    }

                    continue;
                }

                if (referenced == Identity)
                {
                    violations.Add($"{module} references {reference}: Identity is nobody's upstream");
                    continue;
                }

                int from = Array.IndexOf(Order, module);
                int to = Array.IndexOf(Order, referenced);

                // Reviews is downstream of everything and appears in no order slot of its own.
                if (from < 0 && module == Reviews)
                {
                    continue;
                }

                if (to < 0 || from < 0 || to > from)
                {
                    violations.Add($"{module} references {reference}, which is downstream of it");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "A module reference runs against the order " + string.Join(" -> ", Order) +
            " (docs/adr/0004). An upstream module that needs a downstream fact declares the interface " +
            "in its own Contracts project and lets the downstream module implement it:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void OnlyTheAccessorsAndPersistenceHoldTheContext()
    {
        // AppDbContext reaches every table in the database. A module holding it directly could write
        // another module's rows with nothing in the way, which is what the accessors exist to prevent.
        List<string> violations = [];

        foreach (string path in SourceTree.SourceFiles())
        {
            string relative = Path.GetRelativePath(SourceTree.FindSourceRoot(), path).Replace('\\', '/');

            if (!SourceTree.WithoutCommentsOrStrings(File.ReadAllText(path)).Contains("AppDbContext", StringComparison.Ordinal))
            {
                continue;
            }

            bool allowed = relative.StartsWith("Infrastructure/Persistence/", StringComparison.Ordinal)
                           || relative.StartsWith("Infrastructure/Database/", StringComparison.Ordinal)
                           // Each module's own accessor, and the composition root that registers the context.
                           || Modules.Any(module => relative == $"Modules/{module}/{module}Db.cs")
                           // Identity's stores are EF's own, generic over the context type (docs/adr/0004).
                           || relative == "Modules/Identity/IdentityServicesRegistration.cs"
                           || relative == "Modules/Identity/IdentityModel.cs"
                           || relative == "Web/Api/Program.cs";

            if (!allowed)
            {
                violations.Add(relative);
            }
        }

        Assert.True(violations.Count == 0,
            "AppDbContext is named outside the accessors, Persistence, Database and the composition root. " +
            "A module reaches its own tables through its accessor:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void ModuleSqlNamesOnlyItsOwnSchema()
    {
        // The schema is what keeps one module's SQL out of another's tables now that one context can
        // see all of them: bookings.bookings is Bookings' to write, and naming it from Catalog is the
        // same violation a project reference would be.
        List<string> violations = [];
        bool foundAny = false;

        foreach (string module in Modules)
        {
            foreach (string path in Directory.EnumerateFiles(ModuleDirectory(module), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(SourceTree.FindSourceRoot(), path).Replace('\\', '/');

                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                string code = SourceTree.WithoutComments(File.ReadAllText(path));

                foreach (Match match in SchemaConstant().Matches(code))
                {
                    foundAny = true;

                    if (match.Groups[1].Value != module)
                    {
                        violations.Add($"{relative} names {match.Value}");
                    }
                }

                // A literal schema name sidesteps the constant and the check above with it. Searched in
                // string literals only: a namespace spelled the same way is not SQL.
                foreach (string literal in SourceTree.StringLiterals(code))
                {
                    foreach (string other in Modules.Where(m => m != module))
                    {
                        if (Regex.IsMatch(literal, $@"\b{other.ToLowerInvariant()}\.\w+"))
                        {
                            violations.Add($"{relative} spells out the {other.ToLowerInvariant()} schema");
                        }
                    }
                }
            }
        }

        Assert.True(foundAny, "No module SQL names a schema constant at all - the scan is broken.");
        Assert.True(violations.Count == 0,
            "A module's SQL names another module's schema (docs/adr/0004):\n  " + string.Join("\n  ", violations));
    }
}
