using BuildingBlocks.Exceptions;
using System.Net;
namespace Reviews.Exceptions;

/// <summary>
///     A StayReview only ever gets one host reply, not a thread - see
///     StayReview.Reply's own doc comment. Thrown by the entity itself
///     rather than checked ahead of time by ReplyToStayReviewHandler, so
///     the invariant holds regardless of caller.
/// </summary>
public sealed class ReviewAlreadyRepliedException(Guid reviewId)
    : AppException($"Review '{reviewId}' already has a reply.", (int)HttpStatusCode.Conflict);
