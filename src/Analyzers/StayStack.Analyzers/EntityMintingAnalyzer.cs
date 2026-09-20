using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;
namespace StayStack.Analyzers;

/// <summary>
///     SS0001: an entity must not mint its own identity.
///     <para>
///         A caller mints the id before the retried delegate, and the factory takes it
///         (docs/adr/0025). An entity that mints its own produces a different one on every attempt,
///         so a retry after a lost acknowledgement cannot find the row its first attempt wrote.
///     </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EntityMintingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SS0001";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "An entity must not mint an identity",
        "{0} mints an identity by calling {1}. Entity factories take a caller-supplied id, minted outside the retried delegate (docs/adr/0025).",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A retry that mints a new id cannot recognise the row its previous attempt committed.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;

        if (!MintingSymbols.Mints(invocation.TargetMethod))
        {
            return;
        }

        INamedTypeSymbol? containing = context.ContainingSymbol.ContainingType;

        if (containing is null || !DerivesFromEntity(containing) || MintingSymbols.IsAllowed(context.ContainingSymbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.Syntax.GetLocation(),
            containing.Name,
            invocation.TargetMethod.ToDisplayString()));
    }

    // By name: SeedWork is not referenced by the analyzer, and every entity in this codebase derives
    // from SeedWork.Abstractions.Entity.
    private static bool DerivesFromEntity(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "SeedWork.Abstractions.Entity")
            {
                return true;
            }
        }

        return false;
    }
}
