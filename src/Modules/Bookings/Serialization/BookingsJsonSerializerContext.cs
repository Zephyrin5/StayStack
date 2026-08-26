using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetBookingsForHost;
using Bookings.Features.GetHostBookings;
using Bookings.Features.GetMyBookings;
using BuildingBlocks.Pagination;
using System.Text.Json.Serialization;
namespace Bookings.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(ConfirmBookingResponse))]
[JsonSerializable(typeof(GetMyBookingsRequest))]
[JsonSerializable(typeof(PagedResponse<BookingSummary>))]
[JsonSerializable(typeof(GetHostBookingsRequest))]
[JsonSerializable(typeof(PagedResponse<HostBookingSummary>))]
[JsonSerializable(typeof(GetBookingsForHostRequest))]
[JsonSerializable(typeof(CancelBookingRequest))]
[JsonSerializable(typeof(CancelBookingResponse))]
public partial class BookingsJsonSerializerContext : JsonSerializerContext;
