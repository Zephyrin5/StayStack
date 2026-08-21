namespace Catalog.Features.HoldAvailability;

public record HoldAvailabilityResponse
{
    public Guid HoldId { get; init; }
    public DateTime HoldExpiresAt { get; init; }
}
