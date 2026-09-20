# Running more than one instance: what breaks, what does not

Investigation for Track B of the remediation plan. Nothing here changes behaviour;
it records what was measured or read, and what the open decisions are.

Everything below assumes the current topology: one Postgres, N identical API
instances behind a load balancer, no Redis and no other shared infrastructure.

## B.1 Scheduled jobs

**Measured.** Two hosts were started against one database and left for 150
seconds (`TwoHostSchedulerProbe`, skipped unless `STAYSTACK_TWO_HOST` is set,
because it has to wait for real minute boundaries). TickerQ's own occurrence
rows for the every-minute job:

```
Bookings.ExpireUnpaidBookings at 19:32:00 status=3 lock_holder=DESKTOP-GLAO37 rows_for_that_tick=1
Bookings.ExpireUnpaidBookings at 19:33:00 status=3 lock_holder=DESKTOP-GLAO37 rows_for_that_tick=1
Bookings.ExpireUnpaidBookings at 19:34:00 status=1 lock_holder=DESKTOP-GLAO37 rows_for_that_tick=1
```

One occurrence row per tick, each carrying a single `lock_holder`. The store
claims an occurrence rather than every instance running its own copy
(`ticker.CronTickerOccurrences` has `lock_holder` and `locked_at`, and so does
`ticker.TimeTickers`).

**Two caveats this probe cannot clear.** Both hosts were in one process on one
machine, and the holder string is the machine name, so it cannot show *which*
host executed - only that one row existed per tick. And if TickerQ identifies an
instance by machine name, replicas that share a hostname (identical containers in
some orchestrators) may be indistinguishable to its takeover and timeout paths.
TickerQ exposes `InstanceIdentifier` and `NodeIdentifier` internally; whether
10.4.0 lets an application set them was not confirmed, and should be before
relying on it.

**Why this is not the load-bearing guarantee anyway.** Every job here is safe to
run twice, by construction rather than by scheduler behaviour:

| Job | What makes a second run harmless |
|---|---|
| `ExpireUnpaidBookingsJob` | `pg_try_advisory_xact_lock` per booking, then `FOR UPDATE SKIP LOCKED`, then re-checks status under the lock. A second runner steps over every row the first holds. |
| `ResolveOutstandingRefundsJob` | Per-obligation transaction; the resolver is idempotent (`MarkRefundPending` is first-wins, the marker update filters `resolved_at IS NULL`). |
| `ExpiredHoldsSweepJob` | One `DELETE ... WHERE status = 'held' AND hold_expires_at <= now`. |
| `PurgeReplayedCheckoutsJob` | One `DELETE ... WHERE created_at <= cutoff`. |
| `ExpiredRefreshTokensSweepJob` | One `ExecuteDelete` filtered on expiry. |

So the risk of a double execution is duplicated work, not duplicated effect.

**Recommendation.** No change needed to run two instances. Before running many,
confirm how TickerQ identifies an instance and set it explicitly per replica.

## B.2 Caches

Three entries, all `HybridCache`, all read paths in Catalog:

| Key | Written by | TTL | Invalidated by |
|---|---|---|---|
| `properties:{city}:{type}:{guests}:{checkIn}:{checkOut}:{page}:{size}` | `GetPropertiesHandler` | 30 s (L1 and L2) | nothing |
| `property:{propertyId}` | `GetPropertyByIdHandler` | 30 s | nothing |
| `price-calendar:{unitId}:{from}:{to}` | `GetPriceCalendarHandler` | 30 s | nothing |

**There is no invalidation anywhere in the codebase** - no `RemoveAsync`, no
tags. Every entry expires on time alone, and that is deliberate: a listing may
briefly over- or under-represent availability, and the exclusion constraint, not
the cache, is what prevents a double booking.

**Cross-instance staleness is therefore already the single-instance staleness.**
No L2 is configured, so `HybridCache` is L1-only (`AddMemoryCache` with a 64 MB
byte budget). N instances means N independent 30-second windows over the same
data rather than one, so a writer's change can be invisible on one instance for
up to 30 seconds after it is visible on another. Nothing in the write paths reads
these caches, so the effect is confined to what a browsing guest sees.

**If that becomes unacceptable**, the options in increasing cost: shorten the TTL
(cheap, more load); add a distributed L2 so all instances miss together (Redis,
and `HybridCache` supports it directly); add tag-based invalidation on write
(correct, and the first design here that needs the write paths to know about the
cache). No change proposed now.

## B.3 Connection budget

- **Pool size is already configurable** and is not set in code: every context
  uses the `AppConnection` string unmodified, so Npgsql pools them together at
  its default `Maximum Pool Size=100` *per instance*. Four instances is a 400
  connection ceiling against a Postgres whose own `max_connections` is typically
  100-200. **This is the first thing that breaks on scale-out**, and it breaks as
  connection refusals under load rather than gradually.
- **Set `Maximum Pool Size` explicitly** in the connection string for any
  multi-instance deployment: instances × pool ≤ `max_connections` minus
  headroom for migrations, psql and the dashboard.
- **PgBouncer in transaction mode is compatible**, as far as the code goes:
  - Advisory locks are all `pg_advisory_xact_lock`/`_shared`/`pg_try_advisory_xact_lock`
    (`AdvisoryLock`), which are transaction-scoped. Session-scoped advisory locks
    would break under transaction pooling; the codebase has none, and ADR-0028
    rejects them for an unrelated reason.
  - No `SET`/`SET LOCAL`, no `LISTEN`/`NOTIFY`, no temporary tables, no session
    state of any kind in application SQL.
  - Npgsql's automatic prepared statements are off by default (`Max Auto Prepare`
    is not set), which is what transaction mode requires. **If that is ever
    turned on, transaction mode breaks** - server-side prepared statements do not
    survive a connection being handed to another client.
  - Every explicit transaction is one connection for its whole life
    (`ITransactionRunner`), which is exactly what transaction mode pools.
- **Required connection-string settings behind PgBouncer**: `Max Auto Prepare=0`
  (the default, stated explicitly so it cannot drift), and `No Reset On Close=true`
  is *not* needed for transaction mode with modern Npgsql. Keep a direct
  connection for migrations: `dotnet ef database update` and advisory-lock-based
  migration locking want a real session.

## B.4 Rate limiting across instances - decision needed

The three policies are in-process fixed windows partitioned by
`ClientNetworkKey.Resolve` (`FixedWindowPolicies`). Each instance keeps its own
counters, so **the effective limit is N × the configured limit** with N
instances, and which instance a caller lands on decides whose budget they spend.

Current defaults, per instance: auth 10/min, holds 20/min, reads 300/min. The
per-client hold cap (`MaxActiveHoldsPerClient`, 25) is **not** affected - it
counts rows in the database, not requests in memory, so it is already correct
across instances.

Four options:

1. **Accept N×** and divide the configured numbers by the instance count. Free,
   and wrong the moment the instance count changes or an instance restarts.
2. **Limit at the edge** (load balancer, CDN, API gateway). No application code,
   one place to reason about, and it sees the real client address without
   depending on forwarded headers being configured correctly. Loses the ability
   to vary limits per endpoint policy unless the edge can route on path.
3. **A distributed limiter** over Redis. Correct and exact, adds infrastructure
   this deployment does not otherwise need, and puts a network hop in front of
   every request the limiter guards.
4. **Sticky sessions** so a caller always meets the same instance. Cheap to
   configure, but it makes the limit depend on the balancer's hashing and breaks
   on rescale.

**Recommendation: 2 at the edge, keeping the in-process limiter as a floor.** The
application limits stay as a backstop for anything that reaches an instance
directly, and the edge carries the real budget. That needs no code change now -
only a decision, and a note in the limiter options that the numbers are
per-instance.

## B.5 Load test plan - decision needed

**Tool: k6.** It is a single binary, scripts are JavaScript, it runs in CI or
locally, and it reports percentiles rather than averages. NBomber would keep the
scenarios in C# beside the code, which is worth something, but it is a heavier
dependency and a worse fit for running against a deployed environment. Nothing
here needs .NET in the load generator.

Three scenarios, each at 1, 2 and 4 instances against one Postgres:

| Scenario | Shape | What it is looking for |
|---|---|---|
| **Search** | `GET /api/catalog/properties` with a city filter and a date range, 95% cache-missing keys | Cache hit ratio per instance (B.2), and whether the composed availability query holds up when it cannot be served from L1 |
| **Hold** | `POST /api/availability/holds`, half the traffic contending for a handful of units | Advisory-lock queueing per unit, serialization-failure retry counts, and the connection ceiling (B.3) |
| **Confirm** | Hold, then `POST /api/bookings`, then initiate and succeed a payment | The full transaction path: lock ordering, retries, and the idempotency record |

Measurements to record for each: p50/p95/p99 latency, error rate by status code,
`xact_rollback` delta (the retry signal the Phase 1 harness already uses), peak
`pg_stat_activity` backends, and cache hit ratio.

**The decision needed**: whether to add k6 to the repository with these
scenarios, and where they run - locally on demand, or in CI against a
docker-compose topology with N replicas. My recommendation is the repository with
a `workflow_dispatch` job, matching the AOT publish trial: committed, runnable,
and off by default.
