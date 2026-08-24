# 0004 - Module boundaries via per-module Contracts projects

**Status:** Accepted

## Context

In a modular monolith, the module boundary is only as real as what the compiler enforces. If any module can add a project reference straight to another module's main project, it can reach its entities, its `DbContext`, its internal handlers - and the "modular" part of modular monolith erodes one convenient shortcut at a time, usually without anyone deciding that should happen.

At the same time, modules genuinely need to talk to each other: Bookings needs to know a unit's price and confirm/release a hold (Catalog); Transactions needs to look up and confirm a booking (Bookings); Bookings needs to resolve/reverse a transaction (Transactions); Identity needs to register a host (Hosts).

A related question: what belongs in the shared kernel (`SeedWork`, `BuildingBlocks`) versus a single module? Early on, several types lived in the shared kernel by default (e.g. `BookingNotPayableException`, `PropertyType`) without a second module actually needing them - "might be shared later" isn't the same as "is shared."

## Decision

**Cross-module access goes through a `<Module>.Contracts` project, never the module's main project.**

- Each module that's referenced by another module exposes a sibling `<Module>.Contracts` project, physically nested inside the owning module's folder but built as a separate assembly (`Catalog.Contracts`, `Bookings.Contracts`, `Hosts.Contracts`, `Transactions.Contracts`).
- `Contracts` projects hold only what's needed for cross-module calls: interfaces (`IUnitLookup`, `IHoldConfirmation`, `IBookingLookup`, `IBookingPaymentConfirmation`, `ITransactionReversal`, `IHostRegistrar`, `IHostAuthorization`, `IHostLookup`) and their DTOs.
- The interface's implementation is an `internal` class living in the *main* module project (`Catalog.Contracts.UnitLookup` implemented by `internal class UnitLookup` in `Catalog`), registered against the interface in that module's own `ServicesRegistration`. Other modules can only ever resolve it through DI against the interface - the concrete type isn't visible outside its own assembly.
- A domain exception that's actually thrown from more than one module's code (not just carried in a shared DTO) lives in the *owning* module's `Contracts` project, not the shared kernel - `BookingNotPayableException` lives in `Bookings.Contracts` because Bookings' own `Booking.Confirm()` and Transactions' `InitiateTransactionHandler` both throw/rely on it, deferring to Bookings' own invariant.
- `SeedWork`/`BuildingBlocks` hold only what's genuinely used by name from two or more modules already - `Entity`, `Currency`, `EntityStatus`, the soft-delete convention, pagination helpers. The test is "does another module actually reference this by name today," not "this looks generic." Types that failed that test (found during an audit) were moved out into the one module that actually used them.

A module's own main `.csproj` explicitly excludes its nested `Contracts` folder from its own compilation (`<Compile Remove="X.Contracts\**"/>`) - without this, SDK-style default globbing compiles the nested project's files into both assemblies (`CS0436` ambiguous-type errors), since the folder is physically inside the parent but is its own project.

## Alternatives considered

- **Modules reference each other's main project directly**, relying on code review discipline to avoid reaching into internals. Rejected: discipline erodes under deadline pressure in a way a missing project reference cannot - the compiler is a much cheaper enforcement mechanism than a reviewer catching every violation.
- **One shared "Contracts" project for the whole solution**, rather than one per module. Rejected: it would still compile-time-couple every module to every other module's contracts even when no relationship exists, and would immediately become the new dumping ground the shared-kernel test above was meant to prevent.
- **A generic shared kernel by default**, moving things out only if they turn out to need to be module-specific. This is what happened before this decision was made explicit, and it's why the audit above was needed - the default direction matters, and "shared until proven otherwise" reliably accumulates more than "local until proven shared."

## Consequences

- A new cross-module capability costs a small amount of ceremony (an interface in `Contracts`, an `internal` implementation, a DI registration) that a direct reference wouldn't. This is the deliberate cost of the boundary being real.
- Adding a *new* module-to-module relationship for the first time also means creating that module's `Contracts` project if it doesn't already have one, including the `Compile Remove` guard in its own `.csproj`.
- Whether a new shared-looking type belongs in `SeedWork`/`BuildingBlocks` or a specific module's `Contracts` project should keep being decided by the same test: is something *else*, right now, actually referencing it by name - not whether it looks reusable.
