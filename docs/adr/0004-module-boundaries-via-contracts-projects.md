# 0004 - Module boundaries via per-module Contracts projects

**Status:** Accepted

## Context

In a modular monolith, the module boundary is only as real as what the compiler enforces. A module that can reference another module's main project can reach its entities, its `DbContext` and its handlers, and the boundary erodes one convenient shortcut at a time.

Modules still need to talk to each other: Bookings needs a unit's price and host (Catalog) and redeems promotions (Promotions); Transactions looks up and confirms bookings (Bookings); Bookings resolves refunds (Transactions); Identity registers hosts (Hosts); Reviews reads bookings and units.

A related question is what belongs in the shared kernel (`SeedWork`, `BuildingBlocks`) versus a single module.

## Decision

**Cross-module access goes through a `<Module>.Contracts` project, never the module's main project.**

- A module referenced by another exposes a sibling `<Module>.Contracts` project, nested in the module's folder but built as a separate assembly: `Catalog.Contracts`, `Bookings.Contracts`, `Hosts.Contracts`, `Promotions.Contracts`, `Transactions.Contracts`.
- `Contracts` projects hold only what cross-module calls need: interfaces (`IUnitLookup`, `IUnitAvailabilityLookup`, `IUnitArchivalGuard`, `IBookingLookup`, `IBookingPaymentConfirmation`, `IBookingSessions`, `IPromotionRedemption`, `ITransactionLookup`, `ITransactionReversal`, `IHostRegistrar`, `IHostAuthorization`, `IHostLookup`), their DTOs, and the options and value types both sides must agree on (`StaySearchPolicyOptions`, `BookingLifecyclePolicyOptions`, `RefundDecision`).
- The implementation is an `internal` class in the implementing module, registered against the interface in that module's `ServicesRegistration`. Other modules resolve it through DI; the concrete type is invisible outside its assembly.
- A domain exception thrown or relied on by more than one module lives in the owning module's `Contracts` project - `BookingNotPayableException` in `Bookings.Contracts`, because `Booking` and `InitiateTransactionHandler` both depend on it.
- `SeedWork` and `BuildingBlocks` hold only what two or more modules already reference by name and that carries no business meaning - `Entity`, `Currency`, `EntityStatus`, the soft-delete convention, pagination, advisory locks. The test is "does another module reference this by name today", not "this looks generic".

A module's main `.csproj` excludes its nested `Contracts` folder (`<Compile Remove="X.Contracts\**"/>`); otherwise SDK default globbing compiles those files into both assemblies (`CS0436`).

### Direction

A `Contracts` reference prevents reaching into internals but says nothing about direction, and a graph built only from `Contracts` references can still be cyclic. Modules are ordered by how far downstream of raw inventory they sit:

`Hosts → Catalog → Promotions → Bookings → Transactions`

`Identity` depends only on `Hosts.Contracts`; `Reviews` consumes others and exposes nothing. **A module may reference an earlier module's `Contracts`, never a later one's.**

When an upstream module needs a fact a downstream module owns, the interface is declared upstream and implemented downstream. Catalog must refuse to archive a unit with a live booking or hold, which only Bookings knows, so `Catalog.Contracts` declares `IUnitArchivalGuard` and `IUnitAvailabilityLookup`, and Bookings implements both. The downstream module already references the upstream `Contracts`, so this costs no new edge.

A shared value follows the same rule. `StaySearchPolicyOptions` is read by Catalog's search and Bookings' hold path, so it lives in `Catalog.Contracts`, the upstream side, rather than where it is enforced.

Tables follow the boundary too. A raw SQL string naming another module's table is invisible to the compiler and is the same violation as a project reference: `GetPriceCalendarHandler` and `GetPropertiesHandler` ask `IUnitAvailabilityLookup` for blocked ranges instead of joining `unit_availability_holds`.

**Known exception:** Bookings references `Transactions.Contracts` (`ITransactionReversal`, `ITransactionLookup`) and Transactions references `Bookings.Contracts`. Refund resolution and payment confirmation run in both directions, and inverting one side is a larger change than the coupling costs today.

## Alternatives considered

- **Modules reference each other's main projects**, relying on review. Rejected: discipline erodes where a missing project reference does not.
- **One solution-wide Contracts project.** Rejected: it couples every module to every other module's contracts and becomes the dumping ground the shared-kernel test prevents.
- **A generic shared kernel by default**, moving things out when they prove module-specific. Rejected: "shared until proven otherwise" accumulates; "local until proven shared" does not.
- **Holds as a separate Availability module.** Rejected: every hold transition is half of a booking transition. See [ADR-0021](0021-availability-is-part-of-bookings.md).

## Consequences

- A new cross-module capability costs an interface, an `internal` implementation and a registration. That is the price of the boundary being real.
- A new module relationship is checked against the direction order before a `ProjectReference` is added: if the target already references the module being edited, directly or transitively, declare the interface on the upstream side instead.
- A new module that is referenced by another needs its own `Contracts` project and the `Compile Remove` guard.
- Cross-module reads cost a round trip where a join would not. Stay search materialises a platform-wide blocked set across that boundary; see [ADR-0026](0026-stay-search-materialises-the-platform-wide-blocked-set.md).
- All modules share one physical Postgres schema, so moving an entity between modules is an EF model change with an empty or idempotent migration, not a data move.
