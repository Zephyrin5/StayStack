# 0015 - A `Money` value type for currency amounts, at the domain boundary only

**Status:** Accepted

## Context

Every currency amount in this codebase was a plain `decimal`, paired with a sibling `Currency` enum property wherever one was needed - `Unit.BasePrice`/`Currency`, `UnitAvailabilityHold.TotalPrice`/`Currency`, `Booking.TotalPrice`/`Currency`, `Transaction.Amount`/`Currency`, `PromotionRedemption.DiscountAmount`/`Currency`. No convention existed for *when* to round a computed amount, or *to how many decimal places* - `PricingCalculator` summed full-precision `decimal` arithmetic across every night of a stay and only ever got quantized implicitly, by whatever scale the destination Postgres column happened to declare (`numeric(10,2)` in most places, an unspecified default - effectively `numeric(18,2)` - on `Booking.TotalPrice`).

That absence of a boundary was a real bug, not a theoretical one: `ConfirmBookingHandler` needed the pre-discount subtotal to compute a redeemed promo code's base, and reconstructed it by adding `hold.TotalPrice + hold.LengthOfStayDiscountAmount` back together - two independently-rounded numbers that don't reliably recover the third. `GetPriceCalendarHandler`'s calendar preview and `HoldAvailabilityHandler`'s actual charged price both called the same `PricingCalculator`, so they could never structurally disagree on *which* rule applied (see ADR-0012) - but nothing stopped them from landing on different final numbers once rounding entered the picture, since neither path rounded at all until the database truncated the result.

Separately, this app supports KWD, which - like BHD, OMR, JOD, and TND - uses **3** decimal places, not 2. Any rounding convention adopted here has to be currency-aware from the start, not bolted on as a two-decimal assumption that happens to work for SAR/AED/USD.

## Decision

A `readonly record struct Money(decimal Amount, Currency Currency)` in `SeedWork.ValueObjects`, constructed only through `Money.Of(decimal amount, Currency currency)`:

- `Money.Of` rounds `Amount` to the currency's own minor-unit digit count (`CurrencyMinorUnits.For(currency)` - KWD: 3, SAR/AED/USD: 2) using `MidpointRounding.ToEven`, immediately, at construction. There is no way to hold an unrounded `Money` value.
- Every arithmetic operator (`+`, `-`, `*` by a scalar `decimal`, `/` by a scalar `decimal`) re-rounds its result through the same rule. A `Money` value in the wild is therefore always a real, payable amount - never an intermediate full-precision fraction waiting to be rounded later.
- `+`/`-` throw a new `CurrencyMismatchException` (a plain `InvalidOperationException`, not an `AppException` - this is an internal invariant violation, never a caller input error) if the two operands carry different currencies.
- `PricingCalculator` operates in `Money` throughout: `ResolveNightlyPrice` returns an already-rounded `Money` for each night (an override or multiplied rate is rounded the moment it's resolved, not at the end of the stay), and `ResolveStayTotal` sums already-rounded nightly values into `Subtotal`. Each night is therefore the same number a guest would ever actually see charged for that night - not a slice of a total that was rounded once, after the fact.
- `UnitAvailabilityHold.Subtotal` (new column) and `Booking.Subtotal` (new column) snapshot this pre-discount total directly, once, at the point it's computed. `ConfirmBookingHandler`'s coupon-base computation now reads `hold.Subtotal` directly instead of reconstructing it via addition - the exact bug this ADR exists to close.

### `default(Money)` and the `Currency` enum

`Currency.KWD` was `0` before this ADR. A `readonly record struct` is a value type - `default(Money)`, produced by array allocation, a deserializer skipping a field, or an EF materialization edge case on a nullable complex property, would have silently been a plausible-looking "0 KWD" instead of something obviously wrong. `Currency` gained a `None = 0` member, shifting `KWD/SAR/AED/USD` to `1..4`; `Money.Of` throws `ArgumentException` if asked to construct a value with `Currency.None`. This cost nothing at the database: `Currency` has always been persisted via `HasConversion<string>()` (confirmed at every configuration site before this change), never the ordinal, so renumbering is invisible to already-stored data. It's also invisible to the wire: every module's JSON serializer context sets `UseStringEnumConverter = true`, so API responses were never exposing the ordinal either.

### Mapping: `ComplexProperty`, not `OwnsOne`, not JSONB

This codebase's only existing value-converted types - `LocalizedText` and `CancellationPolicy` - both collapse to a single `jsonb` column via a global `Properties<T>()` convention in `StayStackDbContext.ConfigureConventions`. `Money` needed a genuinely different shape: two plain relational columns (`numeric` + `varchar(3)`), so a total can still be summed/indexed/queried in SQL, not buried in a JSON blob. `OwnsOne` and `ComplexProperty` were both entirely unused in this codebase before this ADR. EF Core 10's native `ComplexProperty` is the correct tool for "one value object, two plain columns" and is what's used here, via a small reusable `ModelBuilderExtensions.ConfigureMoney(amountColumnName, currencyColumnName)` helper (same idea as `ApplySoftDeleteQueryFilter` - one place, reused per entity) that pins explicit column names/types so introducing `Money` is a type-only change against already-existing columns wherever the names match, not a rename.

Every money column is standardized on `numeric(12,3)` - scale 3 covers KWD without truncation; every prior column was either `numeric(10,2)` or (on `Booking.TotalPrice`) an unspecified default. This *is* a real physical column-type change (`ALTER COLUMN TYPE`, a table rewrite), not a no-op migration - the empty-`Up`/`Down` pattern ADR-0011 documents only applies when nothing physically changes, which isn't the case for any money column here.

### A second EF Core limitation, discovered mid-implementation

Every entity here (`Booking`, `Unit`, `Transaction`) follows this codebase's established pattern (see `Property.cs`'s own doc comment) of materializing through a real, validated constructor rather than a parameterless one plus `required`/`null!`. EF Core's constructor-binding convention, however, only matches constructor parameters against an entity's *directly* mapped scalar/converted properties by name - it has no notion of binding a parameter to a complex property spanning two columns, and threw `InvalidOperationException: No suitable constructor was found` the first time this was tried. The fix is the standard EF-documented pattern for constructors the binding convention can't fully resolve: each affected entity now also has a `private` **parameterless** constructor, used only as EF's materialization fallback. `Create()` is completely unaffected and still runs through the full validated constructor for every write; the parameterless constructor is never reachable from application code, and any non-nullable reference-type property it would otherwise leave in a warning state gets a real (not `null!`) empty/default placeholder that EF overwrites via property-setting the instant materialization returns.

### Scope boundary: domain-only, deliberately

`Money` replaces `decimal`+`Currency` in: entities (`Unit.BasePrice`, `UnitAvailabilityHold.TotalPrice`, `Booking.TotalPrice`, `Transaction.Amount`, `PromotionRedemption.DiscountAmount`), `PricingCalculator`, and the cross-module `Contracts` records that carry a domain-meaningful amount between modules (`Catalog.Contracts.UnitSummary.BasePrice`, `ConfirmedHold.TotalPrice`, `Bookings.Contracts.BookingSummary.TotalPrice`, `ITransactionReversal`'s refund parameter, `IPromotionRedemption`'s subtotal parameter and result). Outward-facing response DTOs are deliberately **not** converted - `HoldAvailabilityResponse`, `ConfirmBookingResponse`, `CancelBookingResponse`, `TransactionSummary`, and friends keep emitting flat `decimal TotalPrice` + `Currency Currency` fields, unpacked from `Money` at the mapping boundary. This is a wire-format-neutral internal-correctness change; no frontend coordination is required. The one exception is `CancelBookingResponse`, which previously reported `RefundAmount` with no `Currency` field at all - an outright gap, not a deliberate omission, closed alongside this work.

### What stayed a plain `decimal`, deliberately

- `PricingRule.OverridePrice`/`Multiplier`/`DiscountPercent` - no paired `Currency` exists at that level (it's implied by the owning `Unit`); wrapping these in `Money` would invent structure that isn't there.
- `Promotion.DiscountValue` - a genuinely discriminated field. For a `FixedAmount` promotion it's a real currency amount with `Currency` set; for `Percentage` it's a bare percentage and `Currency` is null *by design* (enforced in `CreatePromotionRequestValidator`). Modeling this pair as `Money?` would null out the discount value itself for every percentage-based promotion. Splitting it into `FixedAmount: Money?` / `Percentage: decimal?` would be a real, separate modeling improvement, but is out of scope here.
- **Superseded in part (see "Amendment: subtotal is a `Money`" below).** `UnitAvailabilityHold.Subtotal`/`LengthOfStayDiscountAmount`, `Booking.Subtotal`, `Transaction.RefundAmount` - each shares its owning entity's one canonical currency by construction (a hold/booking/transaction has exactly one currency; `Transaction.MarkRefundPending(Money refundAmount)` validates the incoming refund's currency against `Amount.Currency` before ever persisting it, then stores just the decimal). Modeling these as independently-currencied `Money?` fields would add a redundant currency column that could only ever agree with the entity's own, for no type-safety benefit - the same reasoning that keeps `PricingRule.OverridePrice` a plain decimal.

## Amendment: subtotal is a `Money`

The "what stayed a plain `decimal`" list above conflated two separate
questions, and got one of them wrong.

**Storage - unchanged, and the original reasoning still holds.** A subtotal
does share its entity's one canonical currency by construction, and a second
currency column could only ever agree with the first. There is still exactly
one `subtotal` column and one `currency`/`total_price_currency` alongside it.
Making `Booking.Subtotal` a `Money` required no migration at all: it is a
`Money`-typed property computed over a private `decimal` backing field, which
`BookingConfiguration` maps to the same column by field name.

**Typing - reversed.** "The currency is implied" does not follow from "the
currency is not stored twice", and treating it as if it did pushed the pairing
out to every consumer. `ConfirmBookingHandler` did it literally:

```csharp
Money couponBase = Money.Of(hold.Subtotal, hold.TotalPrice.Currency);
```

That line is the whole argument against the original decision. It is a
currency being re-attached by hand, at a call site, to a value that already
had one - in a codebase whose stated reason for having `Money` at all is that
amounts should carry their currency. Nothing stops the next such line pairing
a subtotal with some *other* amount's currency, and nothing would catch it:
both operands are the right types, the arithmetic succeeds, and
`CurrencyMismatchException` never fires because the mismatch was introduced
before any operator saw it. The same reattachment had already been copied into
a unit test's mock setup, which is how this kind of thing spreads.

So `StayPricingResult.Subtotal`, `ConfirmedHold.Subtotal` and
`ConfirmedHold.LengthOfStayDiscountAmount`, and `Booking.Subtotal` are now
`Money`. `PricingCalculator.StayPriceBreakdown.Subtotal` always was - the type
was being *discarded* at the contract boundary (`Subtotal = breakdown.Subtotal.Amount`)
and manually reconstituted downstream. The currency is now paired back exactly
once, in `HoldConfirmation.ConfirmHoldAsync`, where the row's single currency
column is read.

`UnitAvailabilityHold.Subtotal` stays a plain `decimal`, deliberately and
consistently with this: that type is a persistence-layer construct (see its
own doc comment), written by hand-rolled Dapper SQL and never loaded through
EF change tracking by business logic. `ConfirmedHold` is the contract business
logic actually consumes, and that is where the typing belongs.
`Transaction.RefundAmount` also stays as-is - `MarkRefundPending(Money)`
already validates the incoming currency against `Amount.Currency` at the only
write path, so the invariant is enforced rather than assumed.

## Amendment: where rounding happens in a stay total

The "Alternatives considered" entry below rejects rounding once at the end of
a stay *for per-night prices*. `ResolveStayTotal` then makes the same choice a
second time, one level up, and that was not written down: the length-of-stay
discount is applied to the already-rounded subtotal, and the discount is
itself rounded.

The consequence is that `Subtotal`, `LengthOfStayDiscountAmount` and `Total`
are all real payable amounts, and `Total` is exactly
`Subtotal - LengthOfStayDiscountAmount`. Under the usual alternative - full
precision throughout, round once at the boundary - the two numbers the guest
is shown need not add up to the one they are charged.

**How much this actually matters was measured, not assumed**, because the
first draft of this amendment overstated it. Since the subtotal is always an
exact multiple of the minor unit (it is a sum of already-rounded nightly
prices), rounding is otherwise translation-invariant and the two policies
agree almost everywhere. They diverge only at exact ties, where
`MidpointRounding.ToEven` inspects the last digit of the discount under one
policy and of the difference under the other, and those digits can have
different parity. A sweep over 200,000 random stays put the divergence at
~0.02% of cases, always by exactly one minor unit.

`PricingCalculatorTests` pins a concrete instance rather than a decorative
one: 45 nights at 191.175 KWD less 13.2% gives a subtotal of 8602.875 and a
discount of 1135.580, so this policy charges **7467.295** where round-at-the-
end charges **7467.296** - and at that same tie, round-at-the-end is exactly
where `8602.875 - 1135.580` stops equalling the total charged. The test was
verified to fail against a round-at-the-end implementation, so it discriminates
between the policies rather than merely describing the current one.

## Alternatives considered

- **JSONB collapse, matching `LocalizedText`/`CancellationPolicy`.** Rejected: money values benefit from staying real `numeric` columns - `SUM(total_price)`, indexing, and reporting queries all need that, and jsonb-serializing a value this central would be a real regression in queryability to save a small amount of mapping ceremony.
- **`OwnsOne`.** Would work, but this codebase has no owned-entity-type precedent anywhere, and `ComplexProperty` is EF Core's more direct, purpose-built answer to "one value object, plain columns" as of EF Core 8+.
- **Round once, at the end of a stay total, rather than per night.** Rejected: it would make a displayed nightly rate not add up to the total a guest sees on their itemized bill in edge cases, and it's the shape of the original bug (round only at the boundary, drift accumulates in between).

## Consequences

- `PricingCalculatorTests` and `GetPriceCalendarHandlerTests`' expected values changed where per-night rounding legitimately produces a different result than rounding once at the end (e.g. a KWD stay at a base price that doesn't divide evenly) - this is correct, not a regression, and is called out explicitly in the tests that exercise it (`ResolveStayTotal_ShouldRoundPerNight_ForThreeDecimalCurrency`).
- Any new money-bearing field should default to `Money` at the point it's computed/stored in a domain entity or cross-module contract, and unpack to flat `decimal`+`Currency` only at the point it's written into a response DTO - not the other way around.
- Any new entity with a `Money`-typed constructor parameter needs the same parameterless-constructor-for-EF pairing described above; this is now the established pattern for future entities, not something to rediscover.
- `ApiJsonTypeInfoResolver` has no reflection fallback by design (see its own doc comment) - if `Money` is ever accidentally added to a response DTO, that surfaces as a runtime 500 on the one endpoint touching it, not a compile error. A test iterating every response DTO through `ApiJsonTypeInfoResolver.Combined` guards against this.
