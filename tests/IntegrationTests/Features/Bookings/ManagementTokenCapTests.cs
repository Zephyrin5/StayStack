using Bogus;
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Entities.Configurations;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using BuildingBlocks.Security;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// A replay mints a management token rather than returning the original, whose plaintext is not
// stored, so the number of live credentials for one booking is chosen by whoever holds the key
// (docs/adr/0022). These cover what bounds it: a cap at mint time, and a sweep once the booking is
// far enough past checkout that BookingAccessChecker refuses the tokens anyway.
[Collection(BookingsCollection.Name)]
public class ManagementTokenCapTests(BookingsFixture factory)
{
    private readonly List<Property> _pendingProperties = [];
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    [Fact]
    public async Task ReplayingPastTheCap_KeepsTheNewestAndEvictsTheOldest()
    {
        Guid holdId = await SeedHeldUnitAsync();
        string key = Guid.NewGuid().ToString();

        ConfirmBookingResponse first = await ConfirmAsync(holdId, key);
        Assert.NotNull(first.ManagementToken);

        // Eight in total: the checkout and seven replays, against a cap of five.
        List<string> tokens = [first.ManagementToken];

        for (int replay = 0; replay < 7; replay++)
        {
            ConfirmBookingResponse replayed = await ConfirmAsync(holdId, key);
            Assert.NotNull(replayed.ManagementToken);
            tokens.Add(replayed.ManagementToken);
        }

        Assert.Equal(BookingManagementTokenConfiguration.MaxLivePerBooking, await CountTokensAsync(first.BookingId));

        // The newest works and the first one minted does not, which is the trade stated in the ADR:
        // a guest holding an old link loses it rather than the table growing without bound.
        Assert.Equal(HttpStatusCode.OK, await ExchangeAsync(first.BookingId, tokens[^1]));
        Assert.Equal(HttpStatusCode.NotFound, await ExchangeAsync(first.BookingId, tokens[0]));

        // The boundary itself: five survive, so the fifth-from-last is the oldest still live.
        Assert.Equal(HttpStatusCode.OK, await ExchangeAsync(first.BookingId, tokens[^BookingManagementTokenConfiguration.MaxLivePerBooking]));
        Assert.Equal(HttpStatusCode.NotFound, await ExchangeAsync(first.BookingId, tokens[^(BookingManagementTokenConfiguration.MaxLivePerBooking + 1)]));
    }

    [Fact]
    public async Task ReplayingUnderTheCap_LeavesEveryTokenLive()
    {
        // The other half of the trade, and the reason the cap is not one: a client that retries a
        // couple of times must not invalidate the link its guest already received.
        Guid holdId = await SeedHeldUnitAsync();
        string key = Guid.NewGuid().ToString();

        ConfirmBookingResponse first = await ConfirmAsync(holdId, key);
        ConfirmBookingResponse second = await ConfirmAsync(holdId, key);

        Assert.Equal(2, await CountTokensAsync(first.BookingId));
        Assert.Equal(HttpStatusCode.OK, await ExchangeAsync(first.BookingId, first.ManagementToken!));
        Assert.Equal(HttpStatusCode.OK, await ExchangeAsync(first.BookingId, second.ManagementToken!));
    }

    [Fact]
    public async Task TheSweep_TakesTokensOfBookingsPastTheWindow_AndLeavesTheRest()
    {
        DateOnly today = CatalogSeeding.Today();

        using IServiceScope scope = factory.Services.CreateScope();
        BookingsDb dbContext = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        int lifetimeDays = scope.ServiceProvider
            .GetRequiredService<IOptions<BookingLifecyclePolicyOptions>>().Value.ManagementTokenLifetimeDaysAfterCheckOut;

        // One day past the window and one day inside it. The checker refuses the first and honours
        // the second, so a sweep that took both would delete a working credential.
        Guid expired = await SeedBookingAsync(dbContext, today.AddDays(-lifetimeDays - 1));
        Guid live = await SeedBookingAsync(dbContext, today.AddDays(-lifetimeDays + 1));

        FakeTimeProvider clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTime.UtcNow);

        ExpiredManagementTokensSweepJob job = new ExpiredManagementTokensSweepJob(
            dbContext, clock, scope.ServiceProvider.GetRequiredService<IOptions<BookingLifecyclePolicyOptions>>());

        await job.SweepAsync(null!, TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountTokensAsync(expired));
        Assert.Equal(1, await CountTokensAsync(live));
    }

    private async Task<Guid> SeedBookingAsync(BookingsDb dbContext, DateOnly checkOut)
    {
        // Straight to the table: a checkout will not sell a stay that already happened, and what is
        // under test is a predicate over check_out.
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid(), null,
            _faker.Name.FullName(), _faker.Internet.Email(), null,
            checkOut.AddDays(-2), checkOut, 2,
            Money.Of(300m, Currency.KWD), Money.Of(300m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait", DateTimeOffset.UtcNow.AddMinutes(30));
        booking.Confirm();

        dbContext.Bookings.Add(booking);
        dbContext.BookingManagementTokens.Add(new BookingManagementToken
        {
            Id = Guid.CreateVersion7(),
            BookingId = booking.Id,
            TokenHash = SecureToken.Hash(SecureToken.Generate()),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return booking.Id;
    }

    private async Task<int> CountTokensAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        BookingsDb dbContext = scope.ServiceProvider.GetRequiredService<BookingsDb>();

        return await dbContext.BookingManagementTokens.AsNoTracking()
            .CountAsync(t => t.BookingId == bookingId, TestContext.Current.CancellationToken);
    }

    private async Task<HttpStatusCode> ExchangeAsync(Guid bookingId, string managementToken)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/bookings/{bookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private async Task<ConfirmBookingResponse> ConfirmAsync(Guid holdId, string idempotencyKey)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = "Jane Guest",
                GuestEmail = "jane@example.com"
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? body = await response.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private async Task<Guid> SeedHeldUnitAsync()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        Unit unit = Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
            context.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            context.AddRange(unit);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        DateOnly checkIn = CatalogSeeding.Today().AddDays(5);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(3),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HoldAvailabilityResponse? hold = await response.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }
}
