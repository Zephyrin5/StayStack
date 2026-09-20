using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using StayStack.Analyzers;
namespace UnitTests.Analyzers;

// SS0001 and SS0002 replace two source-scanning tests, so these are what stand behind ADR-0025's
// identity rule now. Each rejected case is a shape the rule exists for; each accepted one is a shape
// the regexes used to get wrong.
public class MintingAnalyzerTests
{
    // The types the rules key on, declared here rather than referenced: the analyzers match by full
    // name, and compiling the real assemblies would test the reference graph instead of the rule.
    // Guid.NewGuid stands in for Guid.CreateVersion7, which the reference assemblies available here
    // predate; the analyzer treats the two identically, and src uses the latter throughout.
    private const string Stubs = """
                                 namespace SeedWork.Abstractions { public abstract class Entity { public System.Guid Id { get; set; } } }
                                 namespace BuildingBlocks.Security { public static class SecureToken { public static string Generate() => string.Empty; } }
                                 namespace BuildingBlocks.Persistence
                                 {
                                     public interface ITransactionRunner
                                     {
                                         System.Threading.Tasks.Task<T> ExecuteAsync<T>(System.Data.IsolationLevel isolation, System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> work, System.Threading.CancellationToken cancellationToken);
                                     }

                                     [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Constructor)]
                                     public sealed class AllowsMintingInRetryAttribute : System.Attribute
                                     {
                                         public AllowsMintingInRetryAttribute(string reason) { }
                                     }
                                 }

                                 """;

    private static Task VerifyAsync<TAnalyzer>(string code)
        where TAnalyzer : DiagnosticAnalyzer, new() =>
        new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + code,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80
        }.RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public Task AnEntityFactoryThatMintsItsOwnId_IsRejected() =>
        VerifyAsync<EntityMintingAnalyzer>(
            """
            public sealed class Booking : SeedWork.Abstractions.Entity
            {
                public static Booking Create() => new Booking { Id = {|SS0001:System.Guid.NewGuid()|} };
            }
            """);

    [Fact]
    public Task AnEntityFactoryTakingItsId_IsAccepted() =>
        VerifyAsync<EntityMintingAnalyzer>(
            """
            public sealed class Booking : SeedWork.Abstractions.Entity
            {
                public static Booking Create(System.Guid id) => new Booking { Id = id };
            }
            """);

    // A type that is not an entity mints freely: the handler minting before the delegate is exactly
    // what ADR-0025 asks for, and a rule that banned it would push the id back inside.
    [Fact]
    public Task AMintingTypeThatIsNotAnEntity_IsAccepted() =>
        VerifyAsync<EntityMintingAnalyzer>(
            """
            public sealed class Handler
            {
                public System.Guid NewId() => System.Guid.NewGuid();
            }
            """);

    [Fact]
    public Task AnIdMintedInsideARetriedDelegate_IsRejected() =>
        VerifyAsync<RetryMintingAnalyzer>(
            """
            public sealed class Handler
            {
                public System.Threading.Tasks.Task<System.Guid> Handle(BuildingBlocks.Persistence.ITransactionRunner runner) =>
                    runner.ExecuteAsync(System.Data.IsolationLevel.ReadCommitted,
                        token => System.Threading.Tasks.Task.FromResult({|SS0002:System.Guid.NewGuid()|}), default);
            }
            """);

    [Fact]
    public Task AnIdMintedBeforeTheDelegate_IsAccepted() =>
        VerifyAsync<RetryMintingAnalyzer>(
            """
            public sealed class Handler
            {
                public System.Threading.Tasks.Task<System.Guid> Handle(BuildingBlocks.Persistence.ITransactionRunner runner)
                {
                    System.Guid id = System.Guid.NewGuid();

                    return runner.ExecuteAsync(System.Data.IsolationLevel.ReadCommitted,
                        token => System.Threading.Tasks.Task.FromResult(id), default);
                }
            }
            """);

    [Fact]
    public Task AnIdMintedThroughAHelperOnTheSameType_IsRejected() =>
        VerifyAsync<RetryMintingAnalyzer>(
            """
            public sealed class Handler
            {
                public System.Threading.Tasks.Task<string> Handle(BuildingBlocks.Persistence.ITransactionRunner runner) =>
                    runner.ExecuteAsync(System.Data.IsolationLevel.ReadCommitted,
                        token => System.Threading.Tasks.Task.FromResult(NewToken()), default);

                private static string NewToken() => {|SS0002:BuildingBlocks.Security.SecureToken.Generate()|};
            }
            """);

    [Fact]
    public Task MintingMarkedAllowedInRetry_IsAccepted() =>
        VerifyAsync<RetryMintingAnalyzer>(
            """
            public sealed class Handler
            {
                public System.Threading.Tasks.Task<string> Handle(BuildingBlocks.Persistence.ITransactionRunner runner) =>
                    runner.ExecuteAsync(System.Data.IsolationLevel.ReadCommitted,
                        token => System.Threading.Tasks.Task.FromResult(NewJti()), default);

                // Stateless and never looked up, so two are equally valid for the same user.
                [BuildingBlocks.Persistence.AllowsMintingInRetry("access tokens are never stored or read back")]
                private static string NewJti() => System.Guid.NewGuid().ToString();
            }
            """);
}
