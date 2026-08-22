# Native AOT migration tracking

`src/Directory.Build.props` sets `IsAotCompatible=true`, which turns on the
trim/AOT analyzers (`IL2xxx`/`IL3xxx`) for every project under `src/`. CI's
"Analyze trimming and Native AOT compatibility" step (`.github/workflows/ci.yml`)
rebuilds `Api.csproj` with `-p:PublishAot=true -p:PublishTrimmed=true` on every
push and is `continue-on-error: true` - it's advisory, not a gate, until this
list is empty. This doc is the log of *why* each currently-suppressed site is
suppressed, so CI going green isn't mistaken for "already AOT-clean," and a
place to note things that aren't warnings yet but will matter as the app grows.

## Currently suppressed

| Site | Warnings | Root cause | Why deferred |
|---|---|---|---|
| `Persistence/ModelBuilderExtensions.cs` - `ApplySoftDeleteQueryFilter` | IL2026, IL3050 | Builds a query-filter `Expression` for each `Entity`-derived CLR type discovered at runtime via `modelBuilder.Model.GetEntityTypes()`. IL2026 (`Expression.Property(Expression, string)`) is fixable in isolation - swap for the `PropertyInfo` overload via a cached `typeof(Entity).GetProperty(nameof(Entity.Status))`. IL3050 (`Expression.Lambda(Expression, ParameterExpression[])`, the untyped overload) is structural: the CLR type is only known at runtime, so there's no compile-time generic parameter to build a typed `Expression<Func<TEntity,bool>>` from. | The only fully AOT-clean fix drops the "every `Entity` subtype gets the filter automatically" convention in favor of one explicit `modelBuilder.Entity<T>().HasQueryFilter(...)` call per entity type - real boilerplate traded for a warning that's currently harmless (app isn't AOT-published). Revisit if/when AOT publishing becomes real. |
| `Persistence/StayStackDbContext.cs` - base constructor | IL2026, IL3050 | `DbContext(DbContextOptions)` itself is annotated `RequiresUnreferencedCode`/`RequiresDynamicCode` inside EF Core - not something app code causes or can silence by rewriting call sites. | Tracks EF Core's own Native AOT support level for the relational/Npgsql provider, not app code. Re-check against the EF Core version in use each time it's upgraded. |

Both were suppressed with `Justification = "<Pending>"` rather than
`"Not utilizing Native AOT execution"` for the query-filter one specifically
because a real fix (the per-entity `HasQueryFilter<T>` rewrite) exists and is
just deferred, not accepted as permanent.

## Already addressed

- `ApiJsonTypeInfoResolver` used to fall back to `new DefaultJsonTypeInfoResolver()`
  (reflection-based) for any type not covered by a module's source-generated
  JSON context - IL2026/IL3050 at that call site. Removed the fallback
  entirely: an uncovered type now throws at serialization time instead of
  silently reflecting, and every JSON-writing path (FastEndpoints,
  `GlobalExceptionHandler`, the 404 page) shares the one resolver. No open
  warning here anymore, and no suppression needed since the fix removed the
  reflection instead of hiding it.
- `PayloadRedactor.Redact<TMessage>`/`GetProperties<TMessage>` - reflects over
  `TMessage`'s public properties, but `TMessage` is a compile-time generic
  parameter, so `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]`
  on the parameter lets the trimmer preserve exactly what's needed. No
  suppression required - this is the pattern to reach for whenever the
  reflected-over type is a generic parameter rather than a runtime-discovered
  one.

## Watch list (not flagged today, worth re-checking as the app grows)

Static analysis only catches what it can prove at compile time - none of
these currently produce a warning, but they're reflection-heavy in ways that
have historically caused *runtime* AOT failures the analyzer doesn't see
until the actual trimmed binary is exercised:

- **ASP.NET Core Identity** (`UserManager<T>`/`SignInManager<T>`) - has had
  partial Native AOT support across .NET versions; worth an actual trimmed
  smoke-test of the auth endpoints before ever publishing AOT for real, not
  just trusting a clean analyzer pass.
- **FastEndpoints.OpenApi / Scalar** - `builder.Services.OpenApiDocument(...)`
  runs unconditionally at startup even though `MapOpenApi`/`MapScalarApiReference`
  are gated behind `IsDevelopment()`; schema generation for minimal APIs is a
  common source of runtime-only trim breakage industry-wide.
- **FluentValidation** - rule definitions build expression trees; currently
  quiet because FastEndpoints' source generator handles validator discovery,
  but worth re-checking if validation rules grow more dynamic (e.g.
  reflection-based cross-property rules).

## When to reassess

The suppression table above should stay small and each row should name a
concrete reason it isn't fixed yet. Treat it as a signal to stop and
re-evaluate whether Native AOT is still the right target - rather than adding
another row - once any of these happen:

- A *new* suppression shows up outside `Persistence` (i.e. in a
  module/feature, not shared infrastructure) - that's a sign the pattern is
  spreading into business logic instead of staying contained to
  infrastructure code written once.
- The table exceeds ~5-6 entries.
- A suppression is needed on a genuine hot path (a query executed per
  request) rather than one-time startup/model-building code.
