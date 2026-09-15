# 0001 - Native AOT compatibility as a design constraint

**Status:** Accepted

## Context

Native AOT publishing (no JIT, no runtime code generation, aggressive trimming) offers smaller images, faster cold start and lower memory. It is at odds with the most common way .NET libraries are built: reflection-based DI, reflection-based JSON serialization, dynamic proxies, runtime `Expression` compilation and `MethodInfo.Invoke` dispatch.

EF Core is the blocker for publishing this application as Native AOT: its NativeAOT support (compiled models, precompiled queries) is experimental, and the `DbContext` constructor carries `RequiresUnreferencedCode` and `RequiresDynamicCode`. Retrofitting AOT compatibility onto a codebase built around reflection-heavy choices is a far larger migration than choosing compatible options while the codebase is small.

## Decision

**Native AOT and trim compatibility are a standing design constraint, checked continuously, with publishing deferred until EF Core supports it.**

- `IsAotCompatible` is `true` for every project under `src` (`src/Directory.Build.props`). It enables the trim, single-file and AOT analyzers, so the compiler flags reflection and dynamic-code patterns as they are written. The AOT-compatible alternative is usually the source-generated one, which is also the faster choice on a JIT runtime.
- CI runs `-p:PublishAot=true -p:PublishTrimmed=true` as an **advisory** step (`continue-on-error: true`). It surfaces `IL2xxx`/`IL3xxx` diagnostics on every change without a native toolchain, and without failing the build on gaps outside this project's control.
- New dependencies are evaluated against this constraint before adoption.

The constraint shaped these choices:

- **Mediator** (source-generated dispatch) over MediatR.
- **Dapper.AOT** (interceptors, `InterceptorsPreviewNamespaces` in `src/Directory.Build.props`) over plain Dapper.
- **A `JsonSerializerContext` per module** rather than the reflective resolver. A request or response type not registered in its module's context fails at runtime.
- **TickerQ** over Hangfire ([ADR-0002](0002-tickerq-for-background-jobs.md)).
- **`CreateSlimBuilder`** in `Program.cs`.
- **The configuration binding source generator** (`EnableConfigurationBindingGenerator`).

### Known diagnostics

Warnings are left visible rather than suppressed, so the list below is what the build shows and a new diagnostic stands out against it.

| Diagnostic | Where | Status |
|---|---|---|
| `IL2026`/`IL3050` on `DbContext(DbContextOptions)` | `StayStackDbContext` | EF Core; waits on EF's NativeAOT support |
| `IL2026`/`IL3050` on `Expression.Property`/`Expression.Lambda` | `ModelBuilderExtensions.ApplySoftDeleteQueryFilter` | Model building; revisit with EF compiled models |
| `IL2026` on `ValidateDataAnnotations` | options registrations in `Program.cs`, `ApiServicesRegistration`, `BookingsServicesRegistration`, `IdentityServicesRegistration`, `ObservabilityServicesRegistration` | Replaceable by the options validation source generator (`[OptionsValidator]`) |
| `IL2026` on `MinLengthAttribute` | `LocalizationSettings`, `AuthTokenConfiguration` | Follows the options validation above |

Generic helpers that pass a type parameter to EF (`CommittedInsertRecovery`, `ConstraintViolations`) carry the `DynamicallyAccessedMembers` annotation EF's `Set<TEntity>` and `FindEntityType` require.

## Alternatives considered

- **Consider AOT only when a deployment needs it.** Rejected: by then the reflection-heavy choices are load-bearing, and the migration is larger and riskier than choosing compatible options up front.
- **A failing AOT check in CI.** Rejected for now: EF Core's diagnostics cannot be fixed in this project, so the check would either always fail or rest on suppressions. It becomes the right shape once EF supports NativeAOT.
- **Suppressing the known diagnostics.** Rejected: suppressions hide the list above, and a suppression justified as "not using Native AOT" argues against the constraint it silences.
- **Dropping the constraint and keeping only the library choices.** Rejected: without the analyzers, reflection-dependent code accumulates unnoticed. Code written while the analyzers were off introduced two new trim warnings, found as soon as they were re-enabled.

## Consequences

- A new `IL2xxx`/`IL3xxx` warning in the build output is a finding: fix it with an annotation or a source-generated alternative, or add it to the table above with its reason.
- Reviving AOT publishing needs EF Core NativeAOT support, the options validation moved to source generation, a `PublishAot` build that is deployed, and the CI step changed from advisory to failing.
- Some choices are less batteries-included than their reflection-based alternatives (TickerQ especially). That trade is deliberate and should keep being made deliberately.
