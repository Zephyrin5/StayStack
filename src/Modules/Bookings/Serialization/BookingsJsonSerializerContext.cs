using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.GetBookingForManagement;
using Bookings.Features.GetBookingsForHost;
using Bookings.Features.GetHostBookings;
using Bookings.Features.GetMyBookings;
using Bookings.Outbox;
using BuildingBlocks.Pagination;
using System.Text.Json.Serialization;
namespace Bookings.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseHoldOutboxMessage))]
[JsonSerializable(typeof(ReverseTransactionOutboxMessage))]
[JsonSerializable(typeof(ReverseRedemptionOutboxMessage))]
[JsonSerializable(typeof(ConfirmBookingRequest))]
[JsonSerializable(typeof(ConfirmBookingResponse))]
[JsonSerializable(typeof(GetMyBookingsRequest))]
[JsonSerializable(typeof(PagedResponse<BookingSummary>))]
[JsonSerializable(typeof(GetHostBookingsRequest))]
[JsonSerializable(typeof(PagedResponse<HostBookingSummary>))]
[JsonSerializable(typeof(GetBookingsForHostRequest))]
[JsonSerializable(typeof(CancelBookingRequest))]
[JsonSerializable(typeof(CancelBookingResponse))]
[JsonSerializable(typeof(GetBookingForManagementRequest))]
[JsonSerializable(typeof(GetBookingForManagementResponse))]
public partial class BookingsJsonSerializerContext : JsonSerializerContext;
