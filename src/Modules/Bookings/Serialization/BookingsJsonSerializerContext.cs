using Bookings.Features.ConfirmBooking;
using System.Text.Json.Serialization;
namespace Bookings.Serialization;

[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(ConfirmBookingResponse))]
public partial class BookingsJsonSerializerContext : JsonSerializerContext;
