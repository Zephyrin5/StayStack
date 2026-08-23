namespace Catalog.Enums;

// Was in SeedWork.Enums - moved here since nothing outside Catalog's own
// code ever referenced it directly (Catalog.Contracts' own DTOs -
// UnitSummary, ConfirmedHold - don't expose it either). Same reasoning
// Bookings.Entities.BookingStatus's own comment already gives for staying
// local: shared only buys you something once a second module genuinely
// needs the same type, not just because a response DTO carrying it is
// public over HTTP.
public enum PropertyType
{
    Hotel = 0,
    Chalet = 1
}
