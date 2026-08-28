using SeedWork.Enums;
namespace Availability.Features.HoldAvailability;

public record HoldAvailabilityResponse
{
    public Guid HoldId { get; init; }
    public DateTime HoldExpiresAt { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }
}
