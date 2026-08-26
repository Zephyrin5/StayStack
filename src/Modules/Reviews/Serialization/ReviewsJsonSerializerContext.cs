using BuildingBlocks.Pagination;
using Reviews.Features.CreateGuestReview;
using Reviews.Features.CreateStayReview;
using Reviews.Features.DeleteGuestReview;
using Reviews.Features.DeleteStayReview;
using Reviews.Features.GetHostStayReviews;
using Reviews.Features.GetPropertyReviews;
using Reviews.Features.ListMyReviewableBookings;
using Reviews.Features.ReplyToStayReview;
using System.Text.Json.Serialization;
namespace Reviews.Serialization;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateStayReviewRequest))]
[JsonSerializable(typeof(CreateStayReviewResponse))]
[JsonSerializable(typeof(GetPropertyReviewsRequest))]
[JsonSerializable(typeof(GetPropertyReviewsResponse))]
[JsonSerializable(typeof(ListMyReviewableBookingsResponse))]
[JsonSerializable(typeof(ReplyToStayReviewRequest))]
[JsonSerializable(typeof(ReplyToStayReviewResponse))]
[JsonSerializable(typeof(GetHostStayReviewsRequest))]
[JsonSerializable(typeof(PagedResponse<StayReviewSummary>))]
[JsonSerializable(typeof(CreateGuestReviewRequest))]
[JsonSerializable(typeof(CreateGuestReviewResponse))]
[JsonSerializable(typeof(DeleteStayReviewRequest))]
[JsonSerializable(typeof(DeleteStayReviewResponse))]
[JsonSerializable(typeof(DeleteGuestReviewRequest))]
[JsonSerializable(typeof(DeleteGuestReviewResponse))]
public partial class ReviewsJsonSerializerContext : JsonSerializerContext;
