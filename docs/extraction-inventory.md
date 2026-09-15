# Extraction inventory

Every place one module's write commits in another module's transaction. Each row is an `IAtomicScope` call site ([ADR-0029](adr/0029-atomic-scopes-for-cross-module-work.md)). Extracting a module out of the shared database means replacing every row that names it with a different coordination mechanism, so this table is the work list for that decision.

`ExtractionInventoryProtocolTests` compares the first three columns with the source. A scope added, removed or changed without updating this table fails the build.

| Call site | Owner | Participants | What the other modules do inside the scope | Extracting a participant would need |
|---|---|---|---|---|
| `BecomeHostHandler.cs` | Identity | Identity, Hosts | Hosts inserts the `Host` the user is linked to | Host registration and the account link committing separately, with recovery for a host with no linked user |
| `ConfirmBookingHandler.cs` | Bookings | Bookings, Promotions, Catalog | Promotions increments the cap and inserts the redemption; Catalog is read (unit lookup) | A redemption that survives a failed checkout, or a reservation finalised after the booking commits |
| `CancelBookingHandler.cs` | Bookings | Bookings, Transactions, Promotions | Transactions records the refund decision; Promotions reverses the redemption | Refund and redemption reversal delivered after the cancellation commits, with a retrying backstop |
| `ExpireUnpaidBookingsJob.cs` | Bookings | Bookings, Promotions | Promotions reverses the redemption | A redemption reversal delivered after the expiry commits, with a retrying backstop |
| `ResolveOutstandingRefundsJob.cs` | Bookings | Bookings, Transactions | Transactions records the refund; Bookings marks the obligation resolved | Refund and marker committing separately, with the transaction status as the authority (the pre-scope design) |
| `MarkTransactionSucceededHandler.cs` | Transactions | Transactions, Bookings | Bookings confirms the booking and sells the hold, under the booking row lock | A succeeded payment whose booking confirmation, or refund, is delivered afterwards |
| `InitiateTransactionHandler.cs` | Transactions | Transactions, Bookings | Bookings is read (payability re-read under `BookingPaymentLock`) | The re-read made against a copy or a call; the advisory lock no longer spanning both databases |
| `DeleteUnitHandler.cs` | Catalog | Catalog, Bookings | Bookings is read (archival guard under `UnitAvailabilityLock`) | The guard checked against a copy or a call; the lock no longer spanning both databases |
| `DeletePropertyHandler.cs` | Catalog | Catalog, Bookings | Bookings is read (archival guard under each unit's lock) | As for `DeleteUnitHandler` |

## Cross-module work outside any scope

- **`HoldAvailabilityHandler`** runs its own Serializable transaction and reads Catalog (`IUnitLookup.GetUnitAsync`) on Catalog's own connection while holding it. A hold therefore uses two pooled connections. It could join a scope with an isolation-level parameter, but that would widen the Serializable envelope over a cross-module read and change the retry characteristics of the most contended path; it is left as a deliberate residual (ADR-0029).
- **Reads outside a transaction** (lookups through `*.Contracts` before a scope opens, response building after it commits) use their module's own connection and are not listed.
