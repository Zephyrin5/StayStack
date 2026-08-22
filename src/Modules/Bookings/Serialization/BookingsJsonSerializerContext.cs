using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetMyBookings;
using System.Text.Json.Serialization;
namespace Bookings.Serialization;

[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(ConfirmBookingResponse))]
[JsonSerializable(typeof(GetMyBookingsRequest))]
[JsonSerializable(typeof(GetMyBookingsResponse))]
public partial class BookingsJsonSerializerContext : JsonSerializerContext;
