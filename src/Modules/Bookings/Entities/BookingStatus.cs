namespace Bookings.Entities;

// Lives here, not SeedWork - nothing outside Bookings needs this yet,
// unlike PropertyType which is already part of another module's public
// request/response DTOs.
public enum BookingStatus
{
    // No code path reaches Confirmed yet - myFatoorah payment integration
    // is still deferred (see README's "Not built yet"). Every booking is
    // created Pending today; Confirmed is reserved for when a payment step
    // actually exists to gate it.
    Pending = 0,
    Confirmed = 1,
    Cancelled = 2
}
