using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
namespace StayStack.Analyzers;

/// <summary>
///     What "minting an identity" means to SS0001 and SS0002: producing a value a later attempt
///     cannot reproduce (docs/adr/0025).
/// </summary>
internal static class MintingSymbols
{
    public const string AllowsMintingAttribute = "BuildingBlocks.Persistence.AllowsMintingInRetryAttribute";

    private static readonly ImmutableHashSet<string> Names =
        ImmutableHashSet.Create("NewGuid", "CreateVersion7", "Generate");

    public static bool Mints(IMethodSymbol method)
    {
        if (!Names.Contains(method.Name))
        {
            return false;
        }

        string containing = method.ContainingType?.ToDisplayString() ?? string.Empty;

        return method.Name switch
        {
            "NewGuid" or "CreateVersion7" => containing == "System.Guid",
            _ => containing.EndsWith("SecureToken", System.StringComparison.Ordinal)
        };
    }

    public static bool IsAllowed(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == AllowsMintingAttribute);
}
