using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetMyBookings;
using BuildingBlocks.Pagination;
using System.Text.Json.Serialization;
namespace Bookings.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(ConfirmBookingResponse))]
[JsonSerializable(typeof(GetMyBookingsRequest))]
[JsonSerializable(typeof(PagedResponse<BookingSummary>))]
public partial class BookingsJsonSerializerContext : JsonSerializerContext;
