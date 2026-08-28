# 0002 - TickerQ for background jobs

**Status:** Accepted

## Context

The application needs recurring background work: sweeping expired `unit_availability_holds`, sweeping expired `refresh_tokens`, and eventually real asynchronous work (email notifications) once that feature is built. This needs a real scheduler - a persistent job store, retry/visibility, and (eventually) support for jobs beyond simple idempotent cleanup sweeps - not a bare `IHostedService` timer loop reimplemented per job.

Hangfire is the default choice for most .NET teams: mature, huge ecosystem, a built-in dashboard, and a very low-friction API (`BackgroundJob.Enqueue(() => Method())`). That ease of use is also exactly the problem: Hangfire's dispatch model captures the call as an expression tree, serializes the method/type/arguments to storage, and later deserializes and invokes it via `MethodInfo.Invoke` - a reflection-heavy pipeline that conflicts directly with [ADR-0001](0001-native-aot-compatibility.md)'s Native AOT commitment. This isn't a minor gap; it's the core of how Hangfire works, with no official Native AOT support and no realistic path to one.

## Decision

Adopt **TickerQ**: a newer, source-generator-based scheduler. Jobs are plain methods marked `[TickerFunction]`, registered at compile time - no runtime reflection in the dispatch path. It has its own EF Core-backed operational store and a dashboard, matching Hangfire's feature set without Hangfire's dispatch model.

Ownership split:
- Each module owns its own jobs as `[TickerFunction]` methods against its own `DbContext` (`Availability.Jobs.ExpiredHoldsSweepJob`, `Identity.Jobs.ExpiredRefreshTokensSweepJob`) - the module knows *what* should happen periodically.
- `Infrastructure/Jobs` owns scheduler registration, the operational store, and the dashboard (gated behind the existing `Administrator` authorization policy via `WithHostAuthentication`, not a second credential store) - infrastructure knows *how* scheduled execution is hosted.

Every job is written to be safe under at-least-once execution (idempotent `DELETE`/`ExecuteDeleteAsync` operations) - a background scheduler should never be assumed to run a job exactly once, TickerQ or otherwise.

## Alternatives considered

- **Hangfire.** Rejected on the AOT conflict above - see [ADR-0001](0001-native-aot-compatibility.md).
- **Quartz.NET.** The more conservative choice: mature, widely used, and structurally closer to AOT-safe (jobs are typed `IJob` classes resolved via DI and invoked through one interface method, not a captured/serialized method call). Genuinely the fallback if TickerQ's maturity risk (below) turns out to matter. No first-party dashboard.
- **Plain `BackgroundService`/`IHostedService` per job, no scheduler library.** Reasonable for exactly the two sweep jobs that exist today, but doesn't scale to the real, known-coming need (email notifications) without eventually needing a real scheduler anyway - and migrating a handful of jobs later costs less than building and then replacing a hand-rolled scheduling layer now.

## Consequences

- **Real maturity risk, accepted deliberately.** TickerQ is new, with a much smaller install base and community than Hangfire or Quartz. Rough edges were found during adoption: `dotnet ef`'s design-time DbContext discovery doesn't find `TickerQDbContext` (it's registered through TickerQ's own internal wiring, not a plain `AddDbContext<T>` call), worked around with an explicit `IDesignTimeDbContextFactory<TickerQDbContext>`.
- If TickerQ's maturity becomes a real operational problem, Quartz.NET is the documented fallback - migrating the small number of jobs this project will realistically have is not expected to be expensive.
- Every future recurring job should default to at-least-once/idempotent design, not assume single delivery.
