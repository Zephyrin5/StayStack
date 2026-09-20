using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
namespace StayStack.Analyzers;

/// <summary>
///     SS0002: retried work must not mint an identity it will need to recognise.
///     <para>
///         The execution strategy cannot tell a failed commit from one whose acknowledgement was
///         lost, so it re-runs the delegate either way. A delegate that mints inside gets a different
///         value on the second attempt, cannot find the row the first attempt wrote, and reports that
///         row as somebody else's conflict (docs/adr/0025).
///     </para>
///     <para>
///         Mark a method <c>[AllowsMintingInRetry("reason")]</c> when a retry producing a different
///         value loses nothing.
///     </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RetryMintingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SS0002";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "Retried work must not mint an identity",
        "This retried delegate mints an identity by calling {0}{1}. Mint it before ExecuteAsync and pass it in, or mark the method [AllowsMintingInRetry] with the reason a retry may produce a different one (docs/adr/0025).",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A retry that mints a new identity cannot recognise its own committed work.");

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

        if (!RunsRetriedWork(invocation.TargetMethod))
        {
            return;
        }

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            foreach (IAnonymousFunctionOperation work in argument.Descendants().OfType<IAnonymousFunctionOperation>())
            {
                Report(context, work);
            }
        }
    }

    private static void Report(OperationAnalysisContext context, IOperation work)
    {
        HashSet<string> followed = new HashSet<string>();

        foreach ((IInvocationOperation minting, string via) in Minting(work, context, followed))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                minting.Syntax.GetLocation(),
                minting.TargetMethod.ToDisplayString(),
                via));
        }
    }

    /// <summary>
    ///     Every minting call the delegate reaches: its own, and those in the methods it calls on the
    ///     same type. Followed through the semantic model rather than by name, so an overload or an
    ///     unrelated method with the same name is not mistaken for one that mints.
    /// </summary>
    private static IEnumerable<(IInvocationOperation Minting, string Via)> Minting(
        IOperation body, OperationAnalysisContext context, HashSet<string> followed, string via = "")
    {
        foreach (IInvocationOperation call in body.Descendants().OfType<IInvocationOperation>())
        {
            if (MintingSymbols.IsAllowed(call.TargetMethod))
            {
                continue;
            }

            if (MintingSymbols.Mints(call.TargetMethod))
            {
                yield return (call, via);
                continue;
            }

            if (!followed.Add(call.TargetMethod.ToDisplayString()))
            {
                continue;
            }

            foreach (IOperation calledBody in SameFileBody(call, context))
            {
                string deeper = $" through {call.TargetMethod.Name}";

                foreach ((IInvocationOperation minting, string _) in Minting(calledBody, context, followed, deeper))
                {
                    yield return (minting, deeper);
                }
            }
        }
    }

    // Only methods whose source is in this file: a semantic model for another tree is not available
    // here, so a helper in another file is invisible - which is why ADR-0025's behavioural tests
    // remain the evidence, and this is the check that catches the shape early.
    private static IEnumerable<IOperation> SameFileBody(IInvocationOperation call, OperationAnalysisContext context)
    {
        SemanticModel? model = call.SemanticModel;

        if (model is null)
        {
            yield break;
        }

        foreach (SyntaxReference reference in call.TargetMethod.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree != model.SyntaxTree)
            {
                continue;
            }

            if (model.GetOperation(reference.GetSyntax(context.CancellationToken), context.CancellationToken) is { } body)
            {
                yield return body;
            }
        }
    }

    // ITransactionRunner.ExecuteAsync, and IExecutionStrategy.ExecuteAsync in any of its overloads -
    // several are extension methods, so the receiver rather than the containing type is what says so.
    private static bool RunsRetriedWork(IMethodSymbol method)
    {
        if (method.Name != "ExecuteAsync")
        {
            return false;
        }

        string containing = method.ContainingType?.ToDisplayString() ?? string.Empty;

        return containing == "BuildingBlocks.Persistence.ITransactionRunner"
               || containing.StartsWith("Microsoft.EntityFrameworkCore.Storage.", System.StringComparison.Ordinal)
               || method.ReceiverType?.AllInterfaces.Any(i =>
                   i.ToDisplayString() == "Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy") == true;
    }
}
