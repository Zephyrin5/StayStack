# 0001 - Native AOT compatibility as a design constraint

**Status:** Accepted

## Context

.NET's Native AOT publishing model (no JIT, no runtime reflection-based codegen, aggressive trimming) offers real deployment benefits - smaller images, faster cold start, lower memory - that matter more as a system moves toward containerized/serverless-style hosting. It's also directly at odds with the easiest, most common way most .NET libraries are built: reflection-based DI, reflection-based JSON serialization, dynamic proxy generation, runtime `Expression`/`MethodInfo.Invoke` dispatch.

Committing to AOT compatibility isn't free - it rules out or complicates otherwise-popular library choices, and .NET's own ecosystem (including EF Core itself, in places) hasn't fully caught up. The question was whether to treat AOT as a stated goal from the start, or bolt it on later once a real deployment need forced the issue.

## Decision

Treat Native AOT/trim compatibility as a live, ongoing constraint from early in the project, not a future migration:

- `IsAotCompatible` is set `true` globally (`src/Directory.Build.props`), applying to every project via MSBuild property inheritance.
- CI runs `-p:PublishAot=true -p:PublishTrimmed=true` as an **advisory** (`continue-on-error: true`) step - it surfaces `IL2xxx`/`IL3xxx` diagnostics on every change without requiring a full native toolchain or blocking merges on gaps that haven't been closed yet (some of which are outside this project's control, e.g. EF Core's own remaining reflection fallbacks).
- Every dependency choice is expected to be evaluated against this constraint before being adopted, not exempted from it.

## Alternatives considered

- **Don't worry about it until AOT publishing is actually needed.** Rejected: retrofitting AOT compatibility onto a codebase already built around reflection-heavy libraries (a DI container's convention-based registration, a full-reflection JSON serializer, a Hangfire-style job dispatcher) is a much larger, riskier migration than choosing AOT-friendly options up front costs. The advisory (non-blocking) CI check specifically avoids the opposite failure mode - freezing progress on a hard requirement the ecosystem isn't fully ready for yet.

## Consequences

This constraint directly shaped several other choices, each with its own smaller cost:

- **Mediator** (source-generated dispatch) over **MediatR** (reflection-based).
- **Dapper.AOT** (interceptor-based, source-generated) over plain Dapper for raw-SQL paths.
- A hand-written `JsonSerializerContext` per module (source-generated `System.Text.Json`) instead of the fully-reflective default resolver.
- **TickerQ** over **Hangfire** for background jobs - see [ADR-0002](0002-tickerq-for-background-jobs.md).

None of these choices are free - they're all less "batteries-included" than their reflection-based alternatives, and some (TickerQ especially) are less mature as a result. That tradeoff is the point of this ADR: it's deliberate, not accidental, and it should keep being made deliberately for future dependencies rather than silently drifting once a popular reflection-heavy library looks convenient.
