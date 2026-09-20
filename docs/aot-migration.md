# Native AOT migration tracking

`src/Directory.Build.props` sets `IsAotCompatible=true`, which turns on the
trim/AOT analyzers (`IL2xxx`/`IL3xxx`) for every project under `src/`. CI's
"Analyze trimming and Native AOT compatibility" step (`.github/workflows/ci.yml`)
rebuilds `Api.csproj` with `-p:PublishAot=true -p:PublishTrimmed=true
-warnaserror` on every push, and **it is a gate**: a new IL2xxx/IL3xxx fails the
build. The suppression below is the one exception, and it is EF Core's own
annotation rather than anything app code does. This doc is the log of *why* each currently-suppressed site is
suppressed, so CI going green isn't mistaken for "already AOT-clean," and a
place to note things that aren't warnings yet but will matter as the app grows.

## Currently suppressed

| Site | Warnings | Root cause | Why deferred |
|---|---|---|---|
| `Persistence/AppDbContext.cs` - base constructor | IL2026, IL3050 | `DbContext(DbContextOptions)` itself is annotated `RequiresUnreferencedCode`/`RequiresDynamicCode` inside EF Core - not something app code causes or can silence by rewriting call sites. | Tracks EF Core's own Native AOT support level for the relational/Npgsql provider, not app code. Re-check against the EF Core version in use each time it's upgraded. |

The remaining suppression is EF Core's own annotation on a constructor this app
has to call, not something app code can rewrite.

## Blocked upstream

### Compiled model, and precompiled queries (EF Core 10.0.11)

`dotnet ef dbcontext optimize` refuses:

> The entity type 'Booking' has a query filter configured. Compiled model can't
> be generated, because query filters are not supported.

It refuses with `--precompile-queries --nativeaot` too, and for the same reason -
the model is scaffolded first, so query precompilation never starts.

Every entity here has a soft-delete query filter, and that is the point of it:
the alternative is every query site remembering `AND status <> archived`, which
is the mistake the filter exists to make impossible (`SoftDeleteFilterTests`
requires one on each entity). Trading it for a compiled model would buy startup
time at the cost of the guarantee.

Nothing to do in application code. Re-run the command on each EF Core upgrade;
if the restriction lifts, the remaining work is generating the model, calling
`UseModel` outside Development, and a drift test that regenerates and compares.

## Already addressed

- `ApplySoftDeleteQueryFilter` built a query-filter `Expression` per CLR type
  discovered through `modelBuilder.Model.GetEntityTypes()` - IL2026 for
  `Expression.Property(Expression, string)` and IL3050 for the untyped
  `Expression.Lambda`, the second of which had no fix short of dropping the
  convention. Each entity's configuration now calls
  `HasSoftDeleteFilter<TEntity>()`, which is generic and builds nothing at
  runtime. What the convention guaranteed - that no entity is missed - is
  `SoftDeleteFilterTests`, which reads the assembled model and fails if any
  `Entity`-derived type has no filter or a filter of another shape.

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
