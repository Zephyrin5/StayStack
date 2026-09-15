# Project guidance

## This codebase

- Read `docs/adr/` before changing Bookings, Transactions, or cross-module
  transaction boundaries.
- Lock order is advisory lock, then row lock. `BookingPaymentLock` on every path
  that cancels a booking. `UnitAvailabilityLock` and `PropertyUnitsLock` around
  archival.
- Identities are minted by callers, outside retry delegates. Entity factories
  take a `Guid id`.
- Failure injection goes through `CommitFaults`. Tests never name an EF
  interceptor directly. In an atomic scope, target the owner's context.
- A write that spans modules runs in one `IAtomicScope` naming every module it
  touches (ADR-0029). Nothing inside a scope calls outside the database. A new
  or changed scope updates `docs/extraction-inventory.md`.
- Protocol tests in `tests/UnitTests/Persistence/` enforce invariants by
  scanning source. If one fails, fix the code. Adding an allow-list entry
  requires a justification written in the entry itself.

## Design decisions

- Choose the simplest design that satisfies the required correctness,
  reliability, security, and performance constraints.
- Address root causes. If a fix adds another exception or recovery branch,
  consider whether changing the underlying design would remove the problem.
- When a mechanism has failed twice for different reasons, replace it rather
  than adding a third condition.
- When the same mistake recurs, change the tooling so it cannot be expressed,
  rather than adding a rule telling people to avoid it.
- Support known requirements. Do not introduce abstractions, infrastructure,
  or service boundaries solely for hypothetical future needs.
- Prefer making invalid states impossible through types, constraints, and
  transaction boundaries over documenting rules callers must remember.
- Preserve module ownership and use established contracts. Read the relevant
  architecture decisions before changing module boundaries or coordination.
- Existing architecture is evidence, not an obligation. Propose changes when
  they materially simplify the system or improve its guarantees.
- State the alternative you rejected and why. A deferral is a decision.

## Before changing code

- Read the implementation, relevant tests, and applicable documentation.
  Do not infer behavior from names, comments, or test titles.
- Identify the invariant being protected and what actually enforces it.
- Trace affected callers, state transitions, and transaction boundaries.
- After identifying a faulty pattern, search for other instances of it.
  Keep unrelated changes out of the task.

## Persistence and asynchronous work

- Follow the documented lock order and transaction protocol across every
  participating path.
- For retried operations, consider both rollback and a successful commit whose
  acknowledgement is lost. A bare `SaveChangesAsync` is retried by the execution
  strategy.
- Ensure retries reconstruct the required state and can recognize their own
  committed work.
- Distinguish retries within one invocation from retries across requests.
- Match constraint violations by constraint name. Matching on SQL state alone
  turns a recoverable duplicate into a false conflict.
- Treat independently committed side effects as separate failure boundaries.
  An effect outside the database (a provider call, a message) runs after the
  commit, driven by state the commit recorded.
- Consider existing persisted data when changing schemas,
  contracts, or serialization. State any deployment assumptions.

## Tests and verification

- Test observable behavior and persisted outcomes, especially at failure
  boundaries. Use a fresh context when checking committed database state.
- Use explicit synchronization for concurrency tests; do not depend on timing,
  sleeps, or a particular scheduler outcome.
- Distinguish failures before commit from failures after commit.
- Source-scanning tests are structural checks, not proof of runtime behavior.
- A test's name must describe what it proves. If the guarded mechanism cannot be
  broken to watch the test fail, say so in the file.
- Investigate failing tests. Change a test when its expectation is wrong or the
  intended behavior has changed; never weaken it merely to obtain a pass.
- Run relevant checks. Report what was verified and what could not be run.

## Comments and documentation

- Describe the current system. Do not narrate how it used to work.
- Be concise. Avoid unnecessary contrast: write "a modular monolith," not
  "a modular monolith, not microservices," unless the distinction resolves a
  real ambiguity.
- Comments explain non-obvious intent, constraints, or behavior. Remove comments
  that restate readable code.
- Explain what enforces an invariant, or make it a test. Avoid unsupported
  guarantees.
- Keep any comment that explains why correct-looking code would be wrong, or why
  code that looks wrong is correct. Migrations with deliberately empty or
  unusual bodies need this.
- Record significant architectural decisions and their tradeoffs in ADRs. When a
  decision changes, replace the ADR. Do not create an ADR for routine
  implementation choices.

## Communication

- Lead with the result, recommendation, or concrete problem.
- Explain consequential tradeoffs briefly.
- Distinguish confirmed defects from assumptions, conditional risks, and
  opportunities for improvement.
- Avoid repeating the task, narrating routine actions, or praising the changes.
- Do not claim correctness, performance improvements, or successful verification
  without supporting evidence.