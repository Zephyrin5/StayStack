using Mediator;
namespace Reviews.Features.ListMyReviewableBookings;

// No fields - the caller's own CustomerId is all this needs, resolved
// server-side via ICurrentUserProvider, same as GetMyBookingsRequest.
// Unpaged - a customer's own count of checkout-passed, not-yet-reviewed
// bookings is small, same reasoning ListPricingRulesResponse already uses.
public record ListMyReviewableBookingsRequest : IRequest<ListMyReviewableBookingsResponse>;
