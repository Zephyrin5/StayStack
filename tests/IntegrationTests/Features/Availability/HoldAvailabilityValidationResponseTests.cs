using Availability.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Availability;

// The response *body* of a rejected hold, not just its status code.
// GlobalExceptionHandler used to map every ArgumentException to a 400
// carrying argEx.Message verbatim, which meant Guard.Against.OutOfRange's
// BCL-formatted message - parameter name and actual value appended - was
// what reached the client, in production. These assert the curated
// ValidationProblemDetails shape that replaced it.
[Collection("Integration Tests")]
public class HoldAvailabilityValidationResponseTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task<Unit> SeedUnitAsync(int maxOccupancy)
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxOccupancy,
            100);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.Add(property);
        context.Add(unit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return unit;
    }

    private async Task<(HttpStatusCode Status, string Body)> PostHoldAsync(Guid unitId, DateOnly checkIn, int guestCount)
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unitId,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = guestCount
            },
            TestContext.Current.CancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hold_GuestCountAboveMaxOccupancy_Returns400WithoutLeakingTheParameterName()
    {
        Unit unit = await SeedUnitAsync(maxOccupancy: 2);
        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5);

        (HttpStatusCode status, string body) = await PostHoldAsync(unit.Id, checkIn, guestCount: 9);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        // The business fact the caller needs, kept verbatim.
        Assert.Contains("maximum occupancy of 2", body);

        // The BCL decoration that used to ride along with it. "(Parameter
        // 'guestCount')" names an internal argument, and ArgumentOutOfRange
        // appends the rejected value too - neither belongs in a public
        // response, and both arrived there purely because the message came
        // off an exception type the handler had no business trusting.
        Assert.DoesNotContain("Parameter", body);
        Assert.DoesNotContain("Actual value", body);
    }

    [Fact]
    public async Task Hold_CheckInInThePast_Returns400AsAFieldLevelError()
    {
        Unit unit = await SeedUnitAsync(maxOccupancy: 4);
        DateOnly pastCheckIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);

        (HttpStatusCode status, string body) = await PostHoldAsync(unit.Id, pastCheckIn, guestCount: 1);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Check-in date cannot be in the past.", body);
        Assert.DoesNotContain("Parameter", body);

        // A ValidationProblemDetails keyed by field, not a flat Detail
        // string - same shape FluentValidation failures already produce, so
        // a client parses one response format for bad input rather than two.
        Assert.Contains("\"errors\"", body);
        Assert.Contains("CheckIn", body);
    }
}
