# 0015 - A `Money` value type for currency amounts, at the domain boundary only

**Status:** Accepted

## Context

A plain `decimal` beside a `Currency` property says nothing about *when* an amount is rounded or *to how many places*, so amounts are quantized implicitly by whatever scale a column declares, and a value derived from two independently rounded numbers does not reliably recover a third. Two paths calling the same `PricingCalculator` agree on which rule applies ([ADR-0012](0012-single-pricing-rule-entity-with-write-time-overlap-rejection.md)) but can land on different final numbers if either rounds differently. And a currency paired back onto an amount by hand, at a call site, can be the wrong one without any operator seeing a mismatch.

Separately, this app supports KWD, which - like BHD, OMR, JOD, and TND - uses **3** decimal places, not 2. Any rounding convention adopted here has to be currency-aware from the start, not bolted on as a two-decimal assumption that happens to work for SAR/AED/USD.

## Decision

A `readonly record struct Money(decimal Amount, Currency Currency)` in `SeedWork.ValueObjects`, constructed only through `Money.Of(decimal amount, Currency currency)`:

- `Money.Of` rounds `Amount` to the currency's own minor-unit digit count (`CurrencyMinorUnits.For(currency)` - KWD: 3, SAR/AED/USD: 2) using `MidpointRounding.ToEven`, immediately, at construction. There is no way to hold an unrounded `Money` value.
- Every arithmetic operator (`+`, `-`, `*` by a scalar `decimal`, `/` by a scalar `decimal`) re-rounds its result through the same rule. A `Money` value in the wild is therefore always a real, payable amount - never an intermediate full-precision fraction waiting to be rounded later.
- `+`/`-` throw a new `CurrencyMismatchException` (a plain `InvalidOperationException`, not an `AppException` - this is an internal invariant violation, never a caller input error) if the two operands carry different currencies.
- `PricingCalculator` operates in `Money` throughout: `ResolveNightlyPrice` returns an already-rounded `Money` for each night (an override or multiplied rate is rounded the moment it's resolved, not at the end of the stay), and `ResolveStayTotal` sums already-rounded nightly values into `Subtotal`. Each night is therefore the same number a guest would ever actually see charged for that night - not a slice of a total that was rounded once, after the fact.
- The pre-discount subtotal is snapshotted once, where it is computed, and read directly by `ConfirmBookingHandler` for a coupon's base - never reconstructed by adding a total and a discount back together.
- **Rounding in a stay total happens per night and per step.** `ResolveStayTotal` sums already-rounded nightly prices into `Subtotal`, rounds the length-of-stay discount, and subtracts it, so `Subtotal`, `LengthOfStayDiscountAmount` and `Total` are each payable amounts and `Total` is exactly `Subtotal - LengthOfStayDiscountAmount`. Rounding once at the end differs only at exact ties, by one minor unit (about 0.02% of stays in a 200,000-stay sweep), and at those ties the itemised amounts no longer add up to the charge. `PricingCalculatorTests` pins a discriminating case: 45 nights at 191.175 KWD less 13.2% charges 7467.295 here and 7467.296 under round-at-the-end.

### `default(Money)` and the `Currency` enum

A `readonly record struct` is a value type, so `default(Money)` - from array allocation, a deserializer skipping a field, or materialization of a nullable complex property - must not be a plausible-looking amount. `Currency.None = 0` is the default, `KWD/SAR/AED/USD` are `1..4`, and `Money.Of` throws `ArgumentException` for `Currency.None`. `Currency` is persisted with `HasConversion<string>()` and serialized with `UseStringEnumConverter = true`, so ordinals appear in neither the database nor the wire format.

### Mapping: `ComplexProperty`, not `OwnsOne`, not JSONB

`LocalizedText` and `CancellationPolicy` collapse to a single `jsonb` column by convention. `Money` maps to two plain relational columns (`numeric` + `varchar(3)`) through EF Core's `ComplexProperty`, so totals stay summable, indexable and queryable in SQL. `ModelBuilderExtensions.ConfigureMoney(amountColumnName, currencyColumnName)` pins the column names and types in one place. Every money column is `numeric(12,3)`; scale 3 covers KWD.

### Entities with a `Money` constructor parameter need a parameterless constructor for EF

Entities materialize through a validated constructor (see `Property.cs`). EF Core's constructor binding matches parameters only to directly mapped properties and cannot bind one to a complex property spanning two columns, so each entity with a `Money` parameter also has a `private` parameterless constructor used only for materialization. `Create()` still goes through the validated constructor for every write, and non-nullable reference properties get real placeholders that EF overwrites.

### Scope boundary: domain-only, deliberately

`Money` replaces `decimal`+`Currency` in: entities (`Unit.BasePrice`, `UnitAvailabilityHold.TotalPrice`, `Booking.TotalPrice`, `Transaction.Amount`, `PromotionRedemption.DiscountAmount`), `PricingCalculator`, and the cross-module `Contracts` records that carry a domain-meaningful amount between modules (`Catalog.Contracts.UnitSummary.BasePrice`, `ConfirmedHold.TotalPrice`, `Bookings.Contracts.BookingSummary.TotalPrice`, `IPaymentReversal`'s payment-state snapshots, `IPromotionRedemption`'s subtotal parameter and result). Outward-facing response DTOs are **not** `Money` - `HoldAvailabilityResponse`, `ConfirmBookingResponse`, `CancelBookingResponse`, `TransactionSummary` and the rest emit flat `decimal` + `Currency` fields, unpacked at the mapping boundary. Every amount a response carries is accompanied by its currency.

### Amounts that share their entity's currency are `Money` in type, not in storage

`Booking.Subtotal`, `Transaction.RefundAmount`, `ConfirmedHold.Subtotal`, `ConfirmedHold.LengthOfStayDiscountAmount` and `StayPricingResult.Subtotal` are `Money`. An amount that shares its entity's one currency does not get a second currency column - it could only ever agree with the first - so the entity stores a private `decimal` backing field and exposes a computed `Money` paired with the entity's currency, mapped to the same column by field name. The currency is paired once, where the row's single currency is read (`HoldConfirmation.ConfirmHoldAsync` for holds), never by a consumer.

`Transaction.MarkRefundPending(Money)` rejects a refund whose currency differs from `Amount`'s. That guard is required, not redundant: only the decimal is stored, so the type would otherwise silently relabel a mismatched refund with the transaction's currency. `TransactionTests` pins both the rejection and the currency that comes back out. A computed `RefundAmount` has no SQL translation, so queries use `EF.Property<decimal?>(t, Transaction.RefundAmountField)`, a `const` on the entity.

### What stays a plain `decimal`

- `UnitAvailabilityHold.Subtotal` - a persistence-layer type written by Dapper and not consumed by business logic; `ConfirmedHold` is the contract that carries the typed value.
- `PricingRule.OverridePrice`/`Multiplier`/`DiscountPercent` - no currency at that level; it is the owning unit's.
- `Promotion.DiscountValue` - a discriminated field: a currency amount for `FixedAmount` promotions, a percentage (with `Currency` null) for `Percentage` ones. Splitting it into `FixedAmount: Money?` / `Percentage: decimal?` would be a separate modelling change.

## Alternatives considered

- **JSONB collapse, matching `LocalizedText`/`CancellationPolicy`.** Rejected: money values benefit from staying real `numeric` columns - `SUM(total_price)`, indexing, and reporting queries all need that, and jsonb-serializing a value this central would be a real regression in queryability to save a small amount of mapping ceremony.
- **`OwnsOne`.** Would work, but this codebase has no owned-entity-type precedent anywhere, and `ComplexProperty` is EF Core's more direct, purpose-built answer to "one value object, plain columns" as of EF Core 8+.
- **Round once, at the end of a stay total, rather than per night and per step.** Rejected: a displayed nightly rate, or a subtotal and discount, would not add up to the charged total at exact ties.
- **A second currency column for amounts that share their entity's currency.** Rejected: it can only agree with the first.
- **Leave shared-currency amounts as bare `decimal`.** Rejected: every consumer then re-attaches a currency by hand, where a wrong pairing type-checks and no operator sees a mismatch.

## Consequences

- Per-night rounding is pinned by `ResolveStayTotal_ShouldRoundPerNight_ForThreeDecimalCurrency` for a KWD stay whose base price does not divide evenly.
- Any new money-bearing field should default to `Money` at the point it's computed/stored in a domain entity or cross-module contract, and unpack to flat `decimal`+`Currency` only at the point it's written into a response DTO - not the other way around.
- Any new entity with a `Money`-typed constructor parameter needs the parameterless constructor described above.
- `ApiJsonTypeInfoResolver` has no reflection fallback by design (see its own doc comment) - if `Money` is ever accidentally added to a response DTO, that surfaces as a runtime 500 on the one endpoint touching it, not a compile error. A test iterating every response DTO through `ApiJsonTypeInfoResolver.Combined` guards against this.
