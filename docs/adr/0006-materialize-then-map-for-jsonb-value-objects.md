# 0006 - Materialize-then-map for JSONB-converted value objects

**Status:** Accepted

## Context

Localized text fields (property names, unit names) are modeled as `LocalizedText`, a value object backed by a `Dictionary<string, string>` and persisted as a single `jsonb` column via an EF Core value converter (`LocalizedTextConverter`, applied globally in `StayStackDbContext.ConfigureConventions`). Several read paths only need a subset of a `LocalizedText`'s data (e.g. `.Values` to flatten it into a plain dictionary for a response DTO) and would ideally project that directly in SQL rather than loading the whole entity.

EF Core can translate simple property access through a value converter, but it cannot translate arbitrary member access (`.Values`, or any other method/property) on the *converted CLR type* into SQL inside a server-side `.Select()` - the conversion only runs when materializing a full entity, not as part of building the query.

## Decision

Every read path that needs to reshape a `LocalizedText` (or similar converted type) loads the full entity first (`.ToListAsync()`/`.SingleOrDefaultAsync()`), then maps to the response shape in memory afterward - never inside `.Select()`. This is consistent across `UnitLookup`, `HoldConfirmation`, `GetPropertyByIdHandler`, and `PropertySummaryMapper`.

## Alternatives considered

- **Raw SQL/Dapper projection**, selecting only the needed columns and deserializing the `jsonb` column directly instead of loading full EF-tracked entities. This is the more efficient answer at scale, and the project already has the tooling for it (`Dapper.AOT` is already in use for other read/write paths). Not adopted for these paths now: at current data volume, loading full entities and mapping in memory isn't a measurable cost, and switching every one of these call sites over is real, currently-unjustified work. Revisit if Catalog's read paths become measurably read-heavy or profiling shows this mattering - not preemptively.
- **A second, non-EF-mapped property on the entity that exposes the raw jsonb string**, so `.Select()` could reference it directly. Rejected: pushes the deserialization problem onto every caller instead of solving it once, and couples callers to the storage representation instead of the value object.

## Consequences

- This pattern should be the default for any new read path touching a `LocalizedText` (or another value-converted type) - reach for materialize-then-map first, not a `.Select()` that will silently fail to translate.
- If this ever needs to change for performance reasons, it should change project-wide (Dapper + raw jsonb projection everywhere it applies), not piecemeal per endpoint - see the alternative above.
