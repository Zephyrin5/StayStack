using BuildingBlocks.Pagination;
namespace Reviews.Features.GetPropertyReviews;

public record GetPropertyReviewsResponse
{
    public required PagedResponse<StayReviewSummary> Reviews { get; init; }
    public required RatingSummary RatingSummary { get; init; }
}

public record StayReviewSummary
{
    public Guid Id { get; init; }
    public decimal OverallRating { get; init; }
    public int CleanlinessRating { get; init; }
    public int CommunicationRating { get; init; }
    public int LocationRating { get; init; }
    public int ValueRating { get; init; }
    public int AccuracyRating { get; init; }
    public string? Comment { get; init; }
    public string? HostReplyText { get; init; }
    public DateTimeOffset? HostRepliedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// Zeroed out (not null) when Count is 0 - an empty property page shows "no
// reviews yet" driven by Count, not by treating the averages as absent.
public record RatingSummary
{
    public int Count { get; init; }
    public decimal AverageOverall { get; init; }
    public decimal AverageCleanliness { get; init; }
    public decimal AverageCommunication { get; init; }
    public decimal AverageLocation { get; init; }
    public decimal AverageValue { get; init; }
    public decimal AverageAccuracy { get; init; }
}
