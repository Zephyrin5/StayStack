# 0001 - Source-generated libraries, without a Native AOT goal

**Status:** Accepted

## Context

.NET's Native AOT publishing (no JIT, no runtime reflection-based code generation, aggressive trimming) offers smaller images and faster cold start, and is at odds with reflection-based DI, JSON serialization, proxy generation and dispatch. EF Core itself still relies on reflection in places this project cannot change.

Nothing about this application's deployment needs AOT today: cold start and image size are not constraints.

## Decision

**Native AOT is not a goal.** There is no `PublishAot` target, `IsAotCompatible` is not set, and new dependencies are not evaluated or excluded on AOT compatibility.

**The source-generated library choices stay, because each earns its place on a normal runtime:**

- **Mediator** over MediatR - source-generated dispatch is faster, and its failures are compile-time.
- **Dapper.AOT** over plain Dapper - the interceptors stay (`InterceptorsPreviewNamespaces` in `src/Directory.Build.props`).
- **A `JsonSerializerContext` per module** - source-generated serialization is faster than the reflective resolver and keeps payload shapes explicit. A request or response type not registered in its module's context fails at runtime.
- **TickerQ** over Hangfire - see [ADR-0002](0002-tickerq-for-background-jobs.md).
- **`CreateSlimBuilder`** in `Program.cs`.

## Alternatives considered

- **Declare AOT compatibility and check it in an advisory CI step.** Rejected: without a built AOT target or a failing check, the declaration produces warnings nobody can act on - mostly `IL2026` from EF Core and `ValidateDataAnnotations` - and suppressions justified as "not utilizing Native AOT". Warnings that cannot be acted on train everyone to skim build output, which is where an actionable warning would appear.
- **Adopt reflection-based libraries by default.** Rejected for the choices above, which are better on a normal runtime too; not rejected as a rule for future dependencies.

## Consequences

- A reflection-using library is acceptable where it is the better choice.
- Reviving AOT needs a deployment reason, a `PublishAot` target that is built, and CI that fails rather than advises. A constraint with nothing enforcing it drifts.
