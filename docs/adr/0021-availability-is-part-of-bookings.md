# 0021 - Holds are part of Bookings

**Status:** Accepted

## Context

A hold (`unit_availability_holds`) and a booking change state together:

| Operation | Hold | Booking |
| --- | --- | --- |
| Confirm a checkout | `held → pending_payment` | insert `Booking` |
| Payment succeeds | `pending_payment → booked` | `Pending → Confirmed` |
| Payment window lapses | `→ held` | `Pending → Cancelled` |
| Cancel | `→ held` | `→ Cancelled` |

None of these is meaningful in half. A released hold under a live booking is a guest with a confirmation and no room; a cancelled booking whose hold is still claimed blocks a range forever. Owned by a separate module upstream of Bookings, a hold could not name the booking every one of these decisions depends on ([ADR-0004](0004-module-boundaries-via-contracts-projects.md)).

Every question worth asking about a hold - should it be released, is the claim still live - is answered by the booking's state.

## Decision

**Holds belong to the Bookings module.** `UnitAvailabilityHold` is mapped in `AppBookingsDbContext`, and each row of the table above is one transaction.

- **`ConfirmBookingHandler`** commits the hold transition with the `Booking`, in one atomic scope that also carries any promotion redemption ([ADR-0003](0003-cross-module-writes-commit-in-one-transaction.md)).
- **`ExpireUnpaidBookingsJob`** and **`CancelBookingHandler`** release the hold in the cancelling transaction.
- **`BookingPaymentConfirmation`** marks the hold paid and confirms the booking under the booking's row lock ([ADR-0020](0020-a-checkout-is-a-claim-with-a-deadline-not-a-sale.md)).

The public routes keep the `availability` group (`POST /api/availability/holds`); the URL is an API concern, not a module layout.

### Dapper statements are handed the caller's transaction

Hold writes are Dapper ([ADR-0014](0014-ef-core-vs-dapper-decision-rule.md)), and Dapper does not discover an ambient EF transaction: a command on an enlisted connection without it either fails or commits independently while the caller believes it is inside their transaction. `HoldConfirmation` passes the current transaction to every statement, and its writes require one (`RequiredTransaction` throws without it), so a caller cannot silently get two-commit semantics.

### The per-client cap counts `pending_payment`

`HoldAvailabilityHandler`'s cap (`MaxActiveHoldsPerClient`, 25 per client network, [ADR-0016](0016-trust-model-for-anonymous-endpoints.md)) counts `held` and `pending_payment` together.

- **Counting `held` alone is escapable for free.** Confirming a checkout moves the row out of the counted set while the exclusion constraint keeps blocking its range. Hold, confirm, repeat: no account, no payment.
- **The rate limit is not a substitute.** Rate limit × payment window bounds concurrent claims at 20/min × 30 min = 600 per client network. Counting `pending_payment` brings that to 25.
- **The cost is a shared budget.** A NAT shares one allowance, so the 26th live claim behind one address gets a 429. These rows are finite - `PaymentDueAt` bounds them - so a full budget drains within the payment window, and the refusal lands at hold time, while the guest is still choosing dates.

`ix_unit_availability_holds_client_key_active` covers the same two statuses; a narrower filter would stop covering the query, which runs inside a Serializable transaction.

## Alternatives considered

- **A separate Availability module upstream of Bookings, joined by atomic scopes.** Rejected: an atomic scope would make each state change commit together, but under [ADR-0004](0004-module-boundaries-via-contracts-projects.md)'s direction rule the hold module still could not name the booking every hold decision depends on.
- **Counting only `held` toward the cap.** Rejected above.

## Consequences

- **Provider-specific mapping lives on the `DbContext`, not in an `IEntityTypeConfiguration`.** `StayRange` is `NpgsqlRange<DateOnly>` over `daterange`, and unit tests build `AppBookingsDbContext` on SQLite, where that mapping fails validation. A configuration class cannot see the provider; `Database.IsNpgsql()` on the context can. `xmin` concurrency tokens use the same shape.
- **`IHoldConfirmation` is public although only Bookings uses it.** Internal would force `ConfirmBookingHandler` and the jobs internal with it, types Mediator, TickerQ and DI discover.
- **A retried `ConfirmBookingHandler` does not touch the hold again.** After a lost acknowledgement it finds its own committed `Booking` by the pre-generated id and returns it; `ConfirmHoldAsync` would fail its `held` guard ([ADR-0025](0025-retried-work-is-built-inside-the-retry.md)).
- **Cross-module effects of a hold state change commit with it.** `ExpireUnpaidBookingsJob` reverses the redemption inside the expiry's atomic scope, so a rollback takes the reversal with it.
- **Payment never marks a cancelled booking's hold sold.** The cancelled-booking check runs under the row lock before the hold transition.
- **Migration `AddAvailabilityToBookingsModel` is idempotent rather than empty.** It adds the table's `client_key`, its partial index and the status `CHECK`, and drops `holder_token`, each guarded (`IF EXISTS` / `IF NOT EXISTS`, a `pg_constraint` lookup), so a database migrated from empty matches the model. Only a fresh-database migration exposes an error of this kind.
