namespace BuildingBlocks.Persistence;

/// <summary>
///     Mutual exclusion between cancelling a booking and opening a payment
///     against it.
///     <para>
///         Cancellation locks the booking row with <c>FOR UPDATE</c>, which is
///         database-wide and would exclude a payment perfectly well - if the
///         payment side could take it. It cannot: naming <c>bookings</c> from
///         Transactions is the coupling docs/adr/0004 exists to prevent, and
///         the same reasoning that produced
///         <see cref="UnitAvailabilityLock"/> applies unchanged. An advisory
///         lock is keyed on a number rather than on anyone's row, so both sides
///         agree on a name without either reaching into the other's schema.
///     </para>
///     <para>
///         Taken <b>exclusively by both</b> sides, unlike the shared/exclusive
///         split for holds. Two concurrent initiations for one booking are
///         already refused by the active-transaction index, so there is no
///         parallelism here worth preserving, and a shared mode would only
///         create a second way to get the pairing wrong.
///     </para>
///     <para>
///         Ordering is unchanged: the booking is locked before the transaction,
///         which is the rule cancellation and payment confirmation already
///         follow. Nothing new is introduced for a deadlock to form around.
///     </para>
/// </summary>
public static class BookingPaymentLock
{
    public static long KeyFor(Guid bookingId) => AdvisoryLock.KeyFor(Scope, bookingId);

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "booking-payment";
}
