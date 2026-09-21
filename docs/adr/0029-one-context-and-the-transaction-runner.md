# 0029 - One DbContext, and the transaction runner

**Status:** Accepted

## Context

[ADR-0003](0003-cross-module-writes-commit-in-one-transaction.md) decides that a cross-module workflow commits as one database transaction. Something has to own that transaction, retry it, and refuse the mistakes that make a shared transaction quietly wrong: work left unsaved at commit time, and entities carried from a failed attempt into the next one.

## Decision

**One `AppDbContext`, and `ITransactionRunner` over it.**

```csharp
Task<T> ExecuteAsync<T>(
    IsolationLevel isolation,
    Func<CancellationToken, Task<T>> work,
    CancellationToken cancellationToken);
```

### The context is assembled, not inherited

`AppDbContext` lives in `Infrastructure/Persistence` and references no module. Each module contributes an `IModuleModel` (`BookingsModel`, `CatalogModel`, ...) that configures its own entities and names its own Postgres schema; `Database/AppDbContextModels` lists the seven, and owns the one migration set and history table.

It declares no `DbSet` properties - it cannot name the modules' types - so every entity names its table in its `IEntityTypeConfiguration`, and a module reaches its own tables through an accessor over the context: `BookingsDb`, `CatalogDb`, `HostsDb`, `IdentityDb`, `PromotionsDb`, `ReviewsDb`, `TransactionsDb`. The accessor is what keeps a module to its own tables now that one context can see all of them, and `ModuleBoundaryTests` fails if a module names `AppDbContext` directly ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)).

Identity's schema is written out in `IdentityModel` rather than inherited from `IdentityDbContext`, which `AppDbContext` cannot derive from, and its stores are registered explicitly (`UserStore<ApplicationUser, IdentityRole<Guid>, AppDbContext, Guid>`) rather than through `AddEntityFrameworkStores`, which resolves them by reflection. Claim tables are not mapped: nothing reads or writes claims.

### The work is the unit of retry

The runner takes the context's execution strategy and runs the work inside it. Each attempt clears the change tracker, begins the transaction at the given isolation level, runs the work, and commits; when it does not commit, the tracker is cleared on the way out. Entities saved and rolled back would otherwise read as `Unchanged` while describing rows that do not exist.

Operations inside the work do not retry on their own - EF suspends execution strategies inside a running one - so an identity a retry must recognise is minted before `ExecuteAsync` ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)). `RetryMintingAnalyzer` (SS0002) makes minting inside the work a build error. It keys on the call: the lambda has to be written at the `ExecuteAsync` argument, and a mint it reaches through a helper is seen only while that helper's body is in the same file. A delegate built elsewhere, or a helper in another file, is outside what the compiler can say - which is why the lost-acknowledgement tests, not the analyzer, are the evidence.

### Contracts save their own changes, and own no transaction

A contract implementation called inside the runner saves what it writes before returning, and never begins, commits or rolls back. Several require the ambient transaction and throw without one, because what they do is meaningless outside it: `HoldConfirmation`, `PromotionRedemption`, `BookingPaymentConfirmation`, `TransactionReversal`, `UnitArchival.EnsureArchivableAsync` and `UnitLookup.IsUnitLiveForWriteAsync` - the last two because a transaction-scoped lock is released before the work it guards otherwise. Dapper statements use `Database.GetDbConnection()` and `CurrentTransaction.GetDbTransaction()`.

Before committing, the runner throws if the change tracker still has changes: work that saved nothing would otherwise commit an empty transaction and report success.

### Nothing inside a transaction leaves the database

No HTTP call, no payment-provider call, no message publish. Such an effect cannot roll back with the transaction and would hold its connection and locks for the duration. `ResolveOutstandingRefundsJob` states this at its call site.

### `HoldAvailabilityHandler` runs its own transaction

It needs Serializable ([ADR-0016](0016-trust-model-for-anonymous-endpoints.md)) where every other path wants Read Committed, and it re-reads the unit under the unit lock with a locking read, because a Serializable snapshot predates the wait for that lock ([ADR-0028](0028-advisory-locks-and-lock-order.md)). It builds its own execution strategy and transaction and stays outside the runner.

## Alternatives considered

- **A context per module, sharing one connection and transaction.** The previous design; see ADR-0003 for why the sharing cost more than it bought.
- **`AppDbContext` deriving from `IdentityDbContext`.** Rejected: it would put Identity's types in `Infrastructure/Persistence` and make every other module's model a subclass of Identity's.
- **A context factory per module.** Rejected: separate instances mean separate transactions again, which is the problem.
- **`TransactionScope`.** Rejected: ambient, and it coordinates separate connections through System.Transactions rather than sharing one.
- **A repository layer over the context**, rather than accessors exposing `DbSet`s. Rejected: it would hide LINQ and the `IQueryable` composition cross-module reads now depend on, for a boundary the project graph and schemas already draw.

## Consequences

- `TransactionRunnerTests` pins rollback across two modules with an EF and a Dapper write, a transient failure retrying the whole transaction without duplicates or stale entities, the unsaved-changes refusal, and that `CommitFaults` fire on the runner's commit.
- **Failure injection targets `AppDbContext`**, which is the only context left.
- **One connection per operation**, holds included: measured 1 for every operation, against 2 for the hold path before.
- **A module can see every table through the one context.** Nothing but the project graph, the accessors and the schema-qualified SQL stops it writing another module's rows, which is why all three are tested rather than documented.
