using BuildingBlocks.Exceptions;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
namespace Catalog.Features.GetPropertyById;

public class GetPropertyByIdHandler(AppCatalogDbContext dbContext, HybridCache cache)
    : IRequestHandler<GetPropertyByIdRequest, GetPropertyByIdResponse>
{
    public async ValueTask<GetPropertyByIdResponse> Handle(GetPropertyByIdRequest request, CancellationToken cancellationToken)
    {
        // A NotFoundException thrown inside the factory propagates straight
        // out of GetOrCreateAsync without being cached - same "only
        // successful results are worth caching" behavior GetPriceCalendarHandler
        // already relies on.
        return await cache.GetOrCreateAsync(
            $"property:{request.PropertyId}",
            async ct => await LoadFromDatabaseAsync(request.PropertyId, ct),
            // Short TTL rather than event-driven invalidation across every
            // Update/Delete Unit/Property handler - same accepted tradeoff
            // as GetPriceCalendarHandler's own cache: a host's just-saved
            // edit being briefly stale on the public property page is
            // acceptable, and doesn't touch anything booking-correctness-
            // critical (price/policy at booking time still comes from a
            // fresh, uncached lookup - see HoldAvailabilityHandler and
            // ConfirmBookingHandler).
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(30)
            },
            cancellationToken: cancellationToken);
    }

    private async Task<GetPropertyByIdResponse> LoadFromDatabaseAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        Property property = await dbContext.Properties.AsNoTracking()
                                .SingleOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
                            ?? throw new NotFoundException(nameof(Property), propertyId);

        var units = await dbContext.Units.AsNoTracking()
            .Where(u => u.PropertyId == propertyId)
            .ToListAsync(cancellationToken);

        return new GetPropertyByIdResponse
        {
            Id = property.Id,
            HostId = property.HostId,
            PropertyType = property.PropertyType,
            Name = new Dictionary<string, string>(property.Name.Values),
            City = property.City,
            Units =
            [
                .. units.Select(u => new UnitSummary
                {
                    Id = u.Id,
                    Name = new Dictionary<string, string>(u.Name.Values),
                    MaxOccupancy = u.MaxOccupancy,
                    BasePrice = u.BasePrice,
                    Currency = u.Currency,
                    CancellationTiers = [.. u.CancellationPolicy.Tiers]
                })
            ]
        };
    }
}
