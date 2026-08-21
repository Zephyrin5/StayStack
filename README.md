# StayStack

A hotel & chalet booking platform API, built as a modular monolith on .NET 10. Customers browse properties, check live availability on a price calendar, and hold a unit while they complete a booking - all backed by a single source of truth for inventory, so no unit ever gets double-booked regardless of which door it's booked through.

This is a backend-first, in-progress reference project. There's no frontend yet. See [Status](#status) for what's built and what's next.

## Architecture

A modular monolith, not microservices: independently-owned modules in one deployable, talking to each other only through explicit contracts.

```
src/
  Core/
    SeedWork/          Domain primitives shared by every module (Entity base, value objects, enums)
    BuildingBlocks/     Cross-cutting abstractions (ICurrentUserProvider, exceptions, authorization policies)
  Infrastructure/
    Persistence/        Shared EF Core conventions (naming, soft-delete filter, Npgsql defaults)
    Identity/            Auth: sign up/in, refresh tokens, JWT issuance
    Observability/       OpenTelemetry wiring (currently disabled, pending Grafana config)
  Modules/
    Catalog/              Properties, units, price calendar, availability holds
    Hosts/                 Host accounts, plus a Hosts.Contracts sub-project other modules depend on instead of Hosts itself
  Web/
    Api/                    FastEndpoints host - the composition root, no business logic of its own
```

A few decisions worth calling out:

- **Module boundaries are compiler-enforced, not just convention.** Catalog and Identity depend on `Hosts.Contracts` (a handful of interfaces, zero dependencies), never on `Hosts` itself - they physically cannot reach `Hosts`' entities or `DbContext`, not just "don't currently."
- **No double-booking, enforced by the database.** Availability holds use a Postgres `EXCLUDE` constraint (via `btree_gist`) on the unit + date range, so two overlapping holds on the same unit can't both commit even under concurrent requests - this isn't application-level locking, Postgres itself rejects the second write.
- **Each module owns its own EF Core migrations and migration history table**, even though all three currently share one physical database. Schema changes in one module can't silently depend on another's.
- **FastEndpoints + Mediator** instead of MVC controllers - each HTTP endpoint is a thin adapter that sends a request through Mediator to a single handler.
- **Source-generated JSON, module by module.** Each module has its own `JsonSerializerContext` for its request/response DTOs; the API host combines them into one resolver with a reflection fallback for framework types it doesn't own (e.g. `ProblemDetails`).

## Tech stack

ASP.NET Core / FastEndpoints, Mediator, EF Core (Npgsql) + Dapper.AOT for hot-path reads, PostgreSQL, ASP.NET Core Identity + JWT bearer auth, xunit v3, Testcontainers, OpenTelemetry

## Getting started

**Prerequisites:** .NET 10 SDK, Docker (for Postgres, and for running the integration tests).

1. **Local secrets** - the connection string and JWT signing key are deliberately not in `appsettings.json`. Set them via the .NET Secret Manager:
   ```
   dotnet user-secrets set "ConnectionStrings:AppConnection" "Host=localhost;Port=5432;Database=StayStack;User Id=postgres;Password=<yours>;" --project src/Web/Api/Api.csproj
   dotnet user-secrets set "Auth:Token:Key" "<any string, 32+ characters>" --project src/Web/Api/Api.csproj
   ```
2. **Start Postgres** (any local instance works, or):
   ```
   docker run -d --name staystack-db -e POSTGRES_PASSWORD=<yours> -e POSTGRES_DB=StayStack -p 5432:5432 postgres:16-alpine
   ```
3. **Apply migrations** - each module owns its own, applied independently against the one database:
   ```
   dotnet ef database update --project src/Infrastructure/Identity/Identity.csproj --startup-project src/Web/Api/Api.csproj --context AppIdentityDbContext
   dotnet ef database update --project src/Modules/Catalog/Catalog.csproj --startup-project src/Web/Api/Api.csproj --context AppCatalogDbContext
   dotnet ef database update --project src/Modules/Hosts/Hosts.csproj --startup-project src/Web/Api/Api.csproj --context AppHostsDbContext
   ```
4. **Run it:**
   ```
   dotnet run --project src/Web/Api/Api.csproj
   ```
   API docs (Scalar) at `/api/docs`, health check at `/health`.

A seeded admin account is available for local testing: `admin@staystack.com` / `1234` (seed-only - see [Status](#status)).

## Testing

- `tests/UnitTests` - validators, domain entity guard clauses, pure logic. No database, no Docker.
- `tests/IntegrationTests` - the full HTTP pipeline against a disposable Postgres container (Testcontainers), spun up and migrated automatically per test run. Requires Docker.

Both test projects use xunit v3's Microsoft.Testing.Platform, which the legacy `dotnet test` command (VSTest-based) doesn't support on the .NET 10 SDK. Build, then run the produced executable directly:

```
dotnet build tests/UnitTests/UnitTests.csproj
dotnet tests/artifacts/bin/UnitTests/Debug/net10.0/UnitTests.dll

dotnet build tests/IntegrationTests/IntegrationTests.csproj
dotnet tests/artifacts/bin/IntegrationTests/Debug/net10.0/IntegrationTests.dll
```

(Output paths are non-standard - see the `BaseOutputPath` override in `src/Directory.Build.props`.)

## CI

`.github/workflows/ci.yml` builds the solution and runs both test suites on every push/PR to `main`.

## Status

**Built:** property/unit catalog (hotels + chalets), JWT auth (sign up, sign in, refresh, become-a-host), admin-assisted host/property creation, availability holds with database-enforced double-booking prevention, a price calendar endpoint (flat pricing only - see below), localization.

**Not built yet:**
- A confirmed `Booking` entity - holds exist, but there's no booking lifecycle (payment, confirmation, e-ticket) built on top of them yet.
- Payment integration, guest checkout, coupons/offers/bundles.
- Rule-based pricing (seasonal/weekend/weekday/holiday/manual override) - `GetPriceCalendarHandler` currently returns each unit's flat base price for every day.
- A frontend - customer site and admin panel are planned once the backend supports a full booking flow end-to-end.
- The seeded admin account's password is a known local-dev value, not something meant to survive a real deployment - a proper credential-rotation flow is planned before that matters.
