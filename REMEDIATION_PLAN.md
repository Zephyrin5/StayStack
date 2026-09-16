# StayStack remediation plan

This plan is executed by Claude Code. Place it at the repository root and delete it when every phase is done.

## How to work through this plan

- Work in order: Phase 0, Phase 1, Phase 2, Phase 3. Tracks A and B start after Phase 1 exits. Phase 4 needs specs first. Do not start anything under "Deferred".
- One branch per phase: `plan/phase-N-<slug>`. One commit per numbered step. Tick the step's checkbox in this file in the same commit.
- After every step run `dotnet build` (no new warnings) and `dotnet test tests/UnitTests`. If a step touches persistence, handlers, jobs, contracts or endpoints, also run `dotnet test tests/IntegrationTests`. Docker must be running for Testcontainers.
- At a STOP, finish the step, report what changed, what you found and anything that surprised you. Then wait for approval.
- If a step's premise is false in the code (a file moved, behaviour differs from what is described here), stop and report. Do not improvise a larger change.

## Standing rules

1. No backward compatibility. The app is pre-launch with no deployed data or clients. Drop dev databases freely. Never write data migrations, backfills, fallbacks for "older rows", or members "kept for existing clients".
2. Native AOT stays a design constraint. `IsAotCompatible=true` stays in `src/Directory.Build.props`. No new reflection-based libraries, no `MakeGenericType` or `Activator` paths, no new trim suppressions without an entry in `docs/aot-migration.md`. Register every new request and response type in its module's `JsonSerializerContext`. Check a new dependency's trim and AOT compatibility before adding it, and state the result in the commit message.
3. ADRs describe the current design. Rewrite an ADR in place when its decision changes. Delete an ADR whose decision no longer exists and remove it from `docs/adr/README.md`. No history sections.
4. Preserve these invariants unless a step explicitly changes them:
   - The `unit_availability_holds_overlap_excl` exclusion constraint, matched by name.
   - The lock order in ADR-0028.
   - The retry protocol in ADR-0025: identities minted before the retried delegate, recognition of the operation's own committed row, constraint violations matched by name through `ConstraintViolations`.
   - Hash-only storage of management tokens, refresh tokens and idempotency keys.
   - Payment and refund behaviour, except the obligation-resolution change in Phase 2. The payments redesign is deferred.
5. Comments state the constraint in one or two lines and link the ADR when one exists. No multi-paragraph rationale in code. When a step edits a file, trim that file's comments to this standard.
6. Schema changes after Phase 1 are ordinary EF migrations. Squash again before the first deployment.

## Phase 0: Baseline cleanup

Goal: production code has no test-only branches, and the unit test project needs no database. No schema changes in this phase.

- [x] **0.1 Remove provider branches.** Delete every `ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"` condition and keep the Postgres path unconditionally: `CancelBookingHandler.cs` (two sites), `BookingPaymentConfirmation.cs`, `ExpireUnpaidBookingsJob.cs` (`supportsSkipLocked`), `InitiateTransactionHandler.cs`, `UnitArchival.cs`, `CreateUnitHandler.cs`, `DeletePropertyHandler.cs`. Delete the `Database.IsNpgsql()` conditionals in `AppBookingsDbContext` and `AppTransactionsDbContext`. Move the Npgsql-only mapping (the daterange mapping, the xmin tokens) into the entity configuration classes and remove `modelBuilder.Ignore<UnitAvailabilityHold>()`.
- [x] **0.2 Remove SQLite from the tests.** Extract the refund computation in `CancelBookingHandler` into a pure static function in Bookings (for example `CancellationRefund.Compute(Money total, CancellationPolicy policy, DateOnly checkIn, DateOnly today)`). Extract the guest-email check into a pure static function. Unit-test both without a database. Move the remaining database-dependent cases of `CancelBookingHandlerTests`, `AuthTokenProviderTests` and `AuditableEntitySaveChangesInterceptorTests` into `tests/IntegrationTests`; drop cases that duplicate `CancelBookingTests`. Delete `tests/UnitTests/Persistence/SingleContextAtomicScope.cs` if nothing uses it. Remove the `Microsoft.EntityFrameworkCore.Sqlite` package from both test projects. Accept: `grep -rn "Sqlite\|ProviderName ==\|IsNpgsql" src tests` returns nothing. Both suites are green.
- [x] **0.3 Remove compatibility surface that needs no schema change.** Remove `CancelBookingResponse.RefundPending`, its tests and its mention in ADR-0027. Remove the `/health` alias in `Program.cs`; keep `/health/live` and `/health/ready`; update `HealthCheckTests` and doc mentions. Delete comments that describe earlier rows or behaviour: `Booking.CancelledAt`, `Booking.TimeZoneId`, `Transaction.SucceededAt`, the older-rows ordering comment in `TransactionReversal`, `ConfirmBookingEndpoint` ("pre-existing behaviour"), the "existing rows" note in `PropertyTimeZone`, the backfill paragraph in ADR-0018.
- [x] **0.4 Make `RefundDecision` require `SucceededAt`.** Make the parameter non-nullable, delete the "SucceededAt unknown" branch, and assert at call sites. Update ADR-0027.
- [ ] **0.5 Fix comments known to be false.** `ClientNetworkKey` references a `HoldSessionCookie` type that does not exist; also fix the cookie mentions in `Program.cs`, and rename `Hold_DiscardingTheHoldSessionCookie_DoesNotGrantAFreshBudget` to describe fresh clients on one network. `JobsServicesRegistration` says `ExpiredHoldsSweepJob` lives in Catalog; it lives in Bookings. `AuthTokenProvider.GenerateScopedToken` claims a caller cannot add Sub or roles; `claims` is caller-supplied and the audience is the protection. Accept: `grep -rn HoldSessionCookie src tests` returns nothing.

**STOP: end of Phase 0.**

## Phase 1: One DbContext

Goal: one `AppDbContext`. Module boundaries are enforced by the project graph, per-module Postgres schemas and architecture tests. `IAtomicScope` is gone.

### Target design

- `AppDbContext` lives in `src/Infrastructure/Persistence`. Sealed, constructor `(DbContextOptions<AppDbContext> options, IEnumerable<IModuleModel> modules)`. `OnModelCreating` adds `btree_gist`, calls each module's `Configure(modelBuilder)`, then applies the soft-delete filter (unchanged until Track A). It absorbs the conventions from `StayStackDbContext`, which is then deleted.
- `IModuleModel` lives in Persistence with one member, `void Configure(ModelBuilder builder)`. Each module implements one (`BookingsModel`, `CatalogModel`, ...), registered as a singleton. Entity configurations call `ToTable(name, schema)` with the module's schema constant: `identity`, `hosts`, `catalog`, `promotions`, `bookings`, `transactions`, `reviews`.
- Module data accessor: each module has one public sealed accessor, for example `BookingsDb(AppDbContext db)`, exposing that module's DbSets through `db.Set<T>()`, plus `Database`, `ChangeTracker`, `Entry` and `SaveChangesAsync`, registered scoped. Handlers, jobs and contract implementations inject their module's accessor, never `AppDbContext`. Every accessor in a request shares the one scoped `AppDbContext`.
- The boundary is the project graph. A module cannot name another module's entity type because it does not reference that module's main project.
- `src/Infrastructure/Database/Database.csproj` references every module and holds the migrations, the design-time factory and, later, the compiled model. Api references it. `MigrationsAssembly` is Database with a single history table.
- `ITransactionRunner` lives in BuildingBlocks with no EF types: `Task<T> ExecuteAsync<T>(IsolationLevel isolation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)`, plus a non-generic overload. The implementation lives in Persistence: the context's execution strategy; each attempt clears the change tracker, begins the transaction, runs the work, throws if `ChangeTracker.HasChanges()`, then commits; the tracker is cleared when the attempt does not commit. Rule: a contract implementation saves its own changes before returning.
- TickerQ's `TickerQDbContext` stays separate with its own migrations.

### Steps

- [ ] **1.0 Capture a baseline.** Run `TransactionTopologyMeasurements` with `STAYSTACK_MEASURE` set; run `HoldAvailabilityConcurrencyTests` and note serialization-failure retry counts if observable. Keep the output outside the repo.
- [ ] **1.1 Add the context, module models and accessors.** Move each module context's `OnModelCreating` content into its module model. Register `AddDbContext<AppDbContext>` once, with the auditable-entity interceptor and `ConfigureStayStackDefaults`. Add the design-time factory in Database. Identity: `AppDbContext` does not derive from `IdentityDbContext`; `IdentityModel` replicates the schema for the entity types in use, claim tables ignored; replace `AddEntityFrameworkStores<AppIdentityDbContext>()` with `UserStore<ApplicationUser, IdentityRole<Guid>, AppDbContext, Guid>` and `RoleStore<IdentityRole<Guid>, AppDbContext, Guid>`; verify the generic constraints accept a plain DbContext. STOP if Identity cannot merge cleanly.
- [ ] **1.2 Schema-qualify all raw SQL.**
- [ ] **1.3 Replace `IAtomicScope` with `ITransactionRunner`.** Call sites: `ConfirmBookingHandler`, `CancelBookingHandler`, `ExpireUnpaidBookingsJob`, `ResolveOutstandingRefundsJob`, `MarkTransactionSucceededHandler`, `InitiateTransactionHandler`, `BecomeHostHandler`, `DeleteUnitHandler`, `DeletePropertyHandler`. Convert other explicit strategy-plus-transaction blocks where the shape fits. `HoldAvailabilityHandler` keeps its explicit Serializable transaction. Delete `IAtomicScope`, `AtomicParticipants`, `AtomicScope`, `AddAtomicParticipant`, the seven module DbContext classes, `SingleContextAtomicScope`, `DbContextRegistrationProtocolTests`, `ExtractionInventoryProtocolTests`, `docs/extraction-inventory.md`, `docs/design/transaction-ownership.md`. Replace `AtomicScopeTests` with `TransactionRunnerTests`. Update the integration tests that referenced the scope. Retarget `CommitFaults` at `AppDbContext`. Update `RetryIdentityProtocolTests` to treat `ITransactionRunner` delegates as retry delegates.
- [ ] **1.4 Remove the Bookings to Transactions.Contracts reference.** Declare in Bookings.Contracts the interface Bookings uses, with its DTOs; Transactions implements it. Delete the ProjectReference and the "Known exception" paragraph in ADR-0004.
- [ ] **1.5 Make search one query.** The blocked-units member of `IUnitAvailabilityLookup` returns `IQueryable<Guid>`; `GetPropertiesHandler` (and `GetPriceCalendarHandler` if it consumes the set) composes it. Integration test asserting one SQL statement. Rewrite or delete ADR-0026.
- [ ] **1.6 Squash migrations.** List every `migrationBuilder.Sql(...)` in the commit message. Make `Booking.CancellationPolicy` non-nullable and remove the `CreateDefault()` fallback. Delete every module Migrations folder (not `src/Infrastructure/Jobs/Migrations`), drop the dev database, add `Initial` in Database, add the hand-written SQL the model cannot express, confirm primary keys precede other unique indexes. Accept: `SchemaInvariantsTests`, `PrimaryKeyConstraintTests`, `ModelHasNoPendingChangesTests` and the full integration suite pass.
- [ ] **1.7 Architecture tests** in `tests/UnitTests/Architecture`: no module main assembly references another's; module order Hosts → Catalog → Promotions → Bookings → Transactions by Contracts only, Identity references only Hosts.Contracts, nothing references Reviews; only accessors, Persistence and Database reference `AppDbContext`; raw SQL in a module names only its own schema.
- [ ] **1.8 Re-measure** with the 1.0 harness. Report peak connections per operation and hold-path serialization retries. STOP if retries rose materially.
- [ ] **1.9 Rewrite ADRs in place:** 0003, 0004, 0010, 0014, 0021, 0025, 0028, 0029 (rewrite as the transaction runner, or delete); update the README index.

**STOP: end of Phase 1.**

## Phase 2: Refund obligations terminate

Context: every cancellation and expiry writes a `RefundObligation`. When no payment exists the resolver returns null and `ResolveOutstandingRefundsJob` retries the row forever at a 6-hour ceiling. Once a cancellation commits no new payment can start, so the only payment that can still succeed is a Pending transaction that already existed.

- [ ] **2.1 Add an outcome column.** `RefundObligationOutcome` (`NothingOwed`, `RefundRecorded`), nullable, set together with `ResolvedAt`. Migration.
- [ ] **2.2 Resolve obligations for every caller.** Succeeded payment: record the refund, `RefundRecorded`. Refund already recorded: `RefundRecorded`. Any Pending payment: leave unresolved. None, or only Failed: `NothingOwed`. `CancelBookingHandler` and `ExpireUnpaidBookingsJob` both call it inside their transactions.
- [ ] **2.3 Resolve on payment failure.** In `MarkTransactionFailedHandler`, inside the runner: if the booking is cancelled and its obligation is unresolved, run the resolver.
- [ ] **2.4 Narrow the sweep.** Select only unresolved obligations whose booking has a Pending transaction, composing an `IQueryable<Guid>` from the payments contract. Keep the backoff. Warn once attempts pass a configurable threshold.
- [ ] **2.5 Integration tests.** 50 expired unpaid bookings leave 50 `NothingOwed`; cancelling unpaid resolves `NothingOwed` immediately; cancel with Pending then fail: `NothingOwed`; cancel with Pending then succeed: full refund, `RefundRecorded`; the sweep's candidate query excludes resolved obligations and those with no open payment; `RefundDeterminismTests` and `RefundCommitBoundaryTests` still pass.
- [ ] **2.6 Rewrite ADR-0027 and the expiry section of ADR-0020.**

**STOP: end of Phase 2.**

## Phase 3: Enforcement and hygiene

- [ ] **3.1 Banned API analyzer.** `Microsoft.CodeAnalysis.BannedApiAnalyzers` under src. Ban `PostgresException.ConstraintName` and `.SqlState` except in `ConstraintViolations.cs` (RS0030 suppressed with a one-line reason). In the integration test project, ban direct `DbTransactionInterceptor` and `DbCommandInterceptor` use except in `CommitFaults`. Delete `ConstraintViolationProtocolTests` and `FaultInjectionProtocolTests`.
- [ ] **3.2 Enforce the payment lock in the type system.** `BookingPaymentLock.AcquireAsync`/`TryAcquireAsync` return a `BookingPaymentLockHandle` (internal constructor, carries the booking id). The booking row claim requires the handle. `Booking.Cancel` requires the handle and checks its booking id. Delete `BookingPaymentLockProtocolTests`.
- [ ] **3.3 Custom Roslyn analyzers.** `src/Analyzers/StayStack.Analyzers` (netstandard2.0), referenced from `src/Directory.Build.props`. SS0001: an `Entity`-derived type must not call `Guid.NewGuid`, `Guid.CreateVersion7` or `SecureToken.Generate`. SS0002: a lambda passed to `ITransactionRunner.ExecuteAsync` or `IExecutionStrategy.ExecuteAsync` must not call them, directly or through a same-type method, followed semantically. Exempt with `[AllowsMintingInRetry("reason")]`. Test with `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing`. Delete `EntityIdentityProtocolTests`, `RetryIdentityProtocolTests`, and `SourceTree.cs` if unused. Keep `SoftDeleteFilterShapeTests` only if it inspects the model. Accept: the only source-scanning test left is the schema check from 1.7.
- [ ] **3.4 Slim `Program.cs`.** Startup cross-checks into `IValidateOptions` with `ValidateOnStart` (booking lifecycle, SameSite versus Secure, CORS versus SameSite, forwarded headers). One helper for the limiter policies, every limiter partitioned by `ClientNetworkKey.Resolve`. Under 150 lines. Update `ForwardedHeadersStartupTests`, `OptionsValidationTests`, `RateLimitingTests`.
- [ ] **3.5 Remove magic values.** Explicit numeric values on `EntityStatus`; constants for raw SQL and index filters, no literal `2`; `HoldAvailabilityHandler.HoldDuration` into options with a validator.
- [ ] **3.6 Comment pass.** One commit per module plus one for Core, Infrastructure and Web. Target under 15% comment lines (measured with the script in the original plan), down from about 33%.

**STOP: end of Phase 3.**

## Track A: AOT readiness

Starts after Phase 1. Runs alongside Phases 2 and 3.

- [ ] **A.1** Make the AOT analysis CI step blocking with warnings as errors.
- [ ] **A.2** JSON coverage test: every FastEndpoints request and response type resolves through `ApiJsonTypeInfoResolver.Combined`.
- [ ] **A.3** Per-entity query filters replace `ApplySoftDeleteQueryFilter`; model-based test; update `docs/aot-migration.md`.
- [ ] **A.4** Compiled model (STOP if a feature in use is unsupported), `UseModel` outside Development, drift test, `scripts/regen-compiled-model.sh`.
- [ ] **A.5** Trial `--precompile-queries` and report failures. No application code changes.
- [ ] **A.6** Scheduled AOT publish trial in CI against Postgres; record blockers in `docs/aot-migration.md`.

## Track B: Scale-out readiness

Starts after Phase 1. Investigate first, then report findings at the STOP before changing code.

- [ ] **B.1** TickerQ across instances: does a cron occurrence run once; is each job safe concurrently; two-host test if feasible.
- [ ] **B.2** Cache consistency: every HybridCache/IMemoryCache entry, its invalidation, cross-instance staleness, proposed TTL or backplane.
- [ ] **B.3** Connection budget: configurable pool size; PgBouncer transaction-mode compatibility; required connection string settings.
- [ ] **B.4** Rate limiting across instances: report options.
- [ ] **B.5** Load test plan: tool and scenarios for hold, confirm and search at 1, 2 and 4 instances.

**STOP: decisions on B.4 and B.5 before implementing.**
