namespace Bookings.Features.CancelBooking;

/// <summary>
///     The second factor a management-link cancellation must supply (see
///     CancelBookingRequest.GuestEmail). Trimmed and case-insensitive; not fixed-time,
///     since the link is the credential and the endpoint is rate limited.
/// </summary>
public static class CancellationGuestEmail
{
    public static bool Matches(string? supplied, string? bookedEmail) =>
        !string.IsNullOrWhiteSpace(supplied)
        && string.Equals(supplied.Trim(), bookedEmail?.Trim(), StringComparison.OrdinalIgnoreCase);
}
