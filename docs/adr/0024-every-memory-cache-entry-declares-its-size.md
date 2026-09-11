# 0024 - Every IMemoryCache entry declares its size, in bytes

**Status:** Accepted

Records a constraint that already governs this codebase but was written down nowhere. Nothing changes at runtime; what changes is that the rule is now discoverable before it is violated rather than after.

## Context

`ApiServicesRegistration` bounds the shared in-process cache:

```csharp
services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
```

That single line carries a constraint most people do not expect:

> **Once `SizeLimit` is set, every entry must specify a `Size`.** `MemoryCache` throws `InvalidOperationException: "Cache entry must specify a value for Size when SizeLimit is set."` on any `Set`/`CreateEntry` that omits it.

The important part is *when* it throws. Not at startup, not in review, not under a smoke test that never populates the entry - on the first request that writes to the cache. A component added by someone who has never seen that registration line works perfectly until the exact moment its cache path runs.

Today there is one consumer, `HybridCache`, which sets each L1 entry's `Size` to its serialized length. So the rule is currently satisfied by a library nobody had to think about, which is precisely why the constraint is invisible: **nothing in the codebase points at it, and no test failed to teach it.** The next component to cache anything learns it by breaking.

## Decision

**Any component that writes to `IMemoryCache` MUST set `Size`, and `Size` MUST be a byte count.**

```csharp
cache.Set(key, value, new MemoryCacheEntryOptions { Size = EstimatedBytes(value) });
```

Two halves, and both matter.

**`Size` must be set** because otherwise the write throws. This is not a sizing preference; it is a correctness requirement of the configuration.

**`Size` must be in bytes** because `SizeLimit` has no unit of its own - it is whatever the entries say it is. `HybridCache` already writes serialized byte lengths into this cache, so a component that wrote `Size = 1` meaning "one entry" would not merely be inconsistent: it would make the 64 MB number meaningless, since the limit would then be counting two different things at once. The budget is shared, so the unit has to be shared too.

### Why the limit exists at all

The read paths behind this cache - `GetProperties`, `GetPropertyById`, `GetPriceCalendar`, `GetPropertyReviews` - are anonymous and keyed on query parameters, so *the caller chooses how many distinct entries exist*, not this application. `MaximumPayloadBytes` bounds one entry; only `SizeLimit` bounds their number. A limit on entry size alone still admits unlimited entries.

### Why one shared budget rather than one per consumer

A keyed or named `IMemoryCache` per component would let each one size itself without interacting. It was rejected: *n* independent limits sum to *n* × limit, and nobody tracks the total. The number that matters is how much of this process's memory the caches may consume altogether, and only a shared budget states it. The cost is real and is stated plainly below.

## Consequences

- **Adding a cache consumer takes headroom from `HybridCache`.** 64 MB is the whole budget, not each component's allowance. A new hot cache does not get its own space; it competes, and the visible symptom is a lower hit rate on the read endpoints rather than an error. Raise the limit deliberately when adding one.
- **Eviction is by size, so a wrong `Size` is worse than no cache.** An entry that under-reports survives eviction pressure it should have lost, and one that over-reports evicts neighbours it should not have. Both are silent. An estimate is fine - `HybridCache`'s serialized length is itself an approximation of retained memory - but it must be an estimate of *bytes*.
- **The rule is now executable.** `MemoryCacheSizeRuleTests` pins it from three directions: the cache is bounded, an entry without a `Size` throws, and an entry with one is accepted. The middle test is the rule itself; the other two stop it being satisfied by a cache that has lost its limit or rejects everything.
- **A structural test fails when a type injects `IMemoryCache` directly**, naming this ADR. That is deliberately a test rather than a paragraph: whoever is about to break this rule is, by definition, someone who has not read it, and a failing test puts the constraint in front of them at the moment it becomes their problem. It is not a ban - taking the dependency is allowed, and adding the type to the test's permitted list is how that decision gets recorded.
- **This says nothing about the distributed tier.** `HybridCache`'s L2 has no size accounting of this kind, and `MaximumPayloadBytes` governs what may enter either tier. The rule here is about the in-process cache only.
