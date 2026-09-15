# 0018 - Business dates resolve in the property's time zone, never UTC

**Status:** Accepted

## Context

A calendar date is only meaningful relative to a place. Bookability, cancellation refund tiers, the review window and the management-token window all compare a stored `DateOnly` against "today". Computed in UTC, that comparison is wrong by a day for part of every day, in a direction that depends on the offset:

- **East of UTC (Kuwait, UTC+3 - `Currency.KWD` is the default market):** UTC's date lags local, so a check-in already past locally is accepted, and a cancellation lands in a *more* generous refund tier than the policy promises.
- **West of UTC:** UTC's date runs ahead, so a same-day booking is rejected and a cancellation drops into a stricter tier a day early.

Either way someone is paid the wrong amount, silently.

## Decision

**Business dates resolve in the property's IANA time zone.** `Property.TimeZoneId` is required on creation and validated, and every business date resolves through `BuildingBlocks.Time.PropertyTimeZone`.

### The property's zone, not the guest's

"Is a check-in still bookable" and "how many days before check-in is this cancellation" are questions about the hotel's calendar, not the browser's. A Toronto guest booking a Kuwait property is measured against Kuwait's today - that is what the guest agreed to, what the host operates on, and it keeps the value server-authoritative rather than dependent on a client-supplied offset.

### At read time, an unusable zone is an error - never a guess

`BuildingBlocks.Time.PropertyTimeZone.Today` throws rather than falling back. There is no `?? "UTC"` anywhere in the request path: a wrong zone is the error this decision exists to prevent, and under a UTC+3 market it loses money. A 500 with a precise message is the honest signal; a quietly mis-computed refund is not. The tradeoff is accepted deliberately: an unresolvable zone blocks a cancellation rather than mispricing it.

The one-time migration backfill is the sole exception. Setting existing rows to `Asia/Kuwait` *is* a guess, and wrong for any property elsewhere - it stands because no better information exists in the data and the column must be populated to become `NOT NULL`. That is a data-migration decision; hosts with properties elsewhere have to correct theirs. Nothing at read time gets the same latitude.

### Coupled sites resolve the same way

`GetBookingForManagementHandler` computes `CanReview = CheckOut <= today` and the three Reviews handlers reject on `CheckOut > today` - the same predicate inverted. Resolving them differently would have the UI offer a review button the API then rejects. `CancelBookingHandler` uses the same `today` for eligibility and the refund tier.

The weakest case for property-local is `BookingAccessChecker`'s management-token window (`ManagementTokenLifetimeDaysAfterCheckOut`, 90 days), which is an access control rather than a business calendar - but it compares against `booking.CheckOut.AddDays(...)`, itself a property-local date, so property-local keeps it comparing like with like.

### Three carriers, all non-nullable

Chosen per site by what that site already has in hand:

- **`UnitSummary.TimeZoneId`** - one more column in the two existing `UnitLookup` projections, alongside `HostId`.
- **`StayPricingResult.TimeZoneId`** - `HoldAvailabilityHandler` awaits `ResolveStayPricingAsync` before it computes `today`; that method **LEFT** joins Properties.
- **`Booking.TimeZoneId`** - snapshotted at confirm from `unit.TimeZoneId`, mirroring `CancellationPolicy`: a host correcting a mis-entered zone must not retroactively move an existing guest's refund boundary or review window. Flows out via `BookingAccessResult.TimeZoneId`.

The snapshot is what lets the booking-scoped sites resolve a date at all. `BookingLookup.VerifyBookingAccessAsync`, `GetBookingForManagementHandler` and `CancelBookingHandler` inject no `IUnitLookup`; the snapshot gives them the zone with zero cross-module calls. `ListMyReviewableBookingsHandler` filters a list spanning many properties before loading any units, so it resolves each booking's date from its own snapshot against one clock reading (`PropertyTimeZone.ToLocalDate`); a single `today` would be wrong there in any zone.

`Booking.TimeZoneId` is **non-nullable**, deliberately departing from `CancellationPolicy`'s nullable-snapshot precedent beside it. A null policy falls back to `CreateDefault()`, a defensible business default; a null zone would fall back to a guess, which is the defect. Same pattern, different stakes, so different nullability; older rows were backfilled by migration rather than left to a runtime fallback.

### `BookingAccessChecker` resolves its own date

The zone is not knowable until the booking is loaded, so `BookingAccessChecker` takes `TimeProvider` and resolves from the loaded booking's snapshot; callers do not pass `today`.

### DST is a non-issue in this direction

Every site converts an *instant* to a *local date*, which is always unambiguous. The ambiguous direction - a wall-clock time that occurs twice or never - never arises, because every comparison is `DateOnly` against `DateOnly`. Pinned by a test across a real Toronto fold.

## Alternatives considered

- **The guest's time zone.** Rejected: it makes the answer depend on who is asking, so the same booking has different cancellation terms for two people, and it puts a client-supplied value on the money path.
- **Inferring the zone from `City`.** Rejected: free text, nullable, and not unique.
- **Optional `TimeZoneId` defaulting to a configured application zone.** Rejected as the friendlier-looking option that quietly recreates the same class of wrong-by-default bug. Making the field required is a **deliberate breaking API change** to `CreateProperty` and `AdminCreateProperty`.
- **`IgnoreQueryFilters()` on the `UnitLookup` joins**, to read properties regardless of archival. Rejected, and worth recording because it is the obvious-looking move: it is *query*-scoped, not join-scoped. Both lookups are one LINQ query rooted on `dbContext.Units`, so it would have disabled soft-delete for **Units** as well and made archived units resolvable through `GetUnitAsync` - which `ConfirmBookingHandler`, `CreateGuestReviewHandler` and `PromotionRedemption.RedeemAsync` all use to reach inventory. Archived units would become confirmable. A regression test pins this.

  It is also unnecessary. "Live unit under an archived property" has no path: `DeletePropertyHandler` is the only caller of `Property.Archive` and archives every unit beneath the property in the *same* `SaveChangesAsync`, and `CreateUnitHandler` resolves its property through the filtered context. **The design depends on that invariant**, which has its own test - if some future admin path archives a property directly, live units beneath it become orphans and the throw below fires, loudly.

## Consequences

- **Orphan handling differs by method, because the callers do.** A Unit with no Property row is a genuine data-integrity violation. `GetUnitAsync` throws `OrphanedUnitException` - the caller wants that one unit, and without its property there is no host to authorize against and no zone to resolve dates in. `GetUnitsAsync` omits the row and continues: all four callers are list endpoints where one bad row must not fail the page, and `IReadOnlyDictionary` already expresses absence. `ResolveStayPricingAsync` uses a **LEFT** join and the same throw, deliberately - an inner join would have made an orphaned unit yield no row, reporting "unit not found" for a unit that exists, so one violation would give two different answers depending on which entry point the guest hit.
- **`OrphanedUnitException` is not an `AppException`**, so it renders as a generic 500. That is the right shape for a data-integrity violation - nothing the caller sent is wrong, and no retry or different input fixes it.
- **Integration fixtures seed a real `Property` for every `Unit`** (`CatalogSeeding`). A bare unit against a throwaway property id is a shape the database should never hold, and tolerating it would force a fallback back into the design.
- **A host may edit a property's zone**, which moves future dates only. Existing bookings keep their snapshot. Live *holds* are the exception and are deliberately left alone: a hold validated under the old zone can be confirmed and snapshotted under a new one if the host edits in between. The window is a hold's 15 minutes and the skew is hours, so it is harmless.
- **Both migrations add the column with a `defaultValue` and then drop it** in the same migration. `defaultValue` otherwise persists as a schema-level `DEFAULT`, so anything inserting outside EF - the hand-written Dapper in this codebase, a manual fix-up - would silently inherit Kuwait. Dropping it makes the column genuinely required at the database level too.
- **A globalization canary test** asserts real tzdata is present and that a DST-observing zone reports different offsets in January and July. `InvariantGlobalization` and `PublishAot` are unset ([ADR-0001](0001-native-aot-compatibility.md)); the test turns enabling either into a test failure rather than an opaque 500 on cancellation.
- Any new business date must resolve through `PropertyTimeZone`, from a property-derived zone. A bare `DateOnly.FromDateTime(timeProvider.GetUtcNow()...)` is always a bug.
