using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Bookings;

// The management token is a bearer credential with a lifetime measured in
// months, exchanged once for a short-lived session (docs/adr/0023). These cover
// the exchange, and the boundaries that keep a session from being worth more
// than the token it came from.
[Collection(BookingsCollection.Name)]
public class BookingSessionTests(BookingsFixture factory)
{
    private readonly List<Property> _pendingProperties = [];
    private readonly HttpClient _client = factory.CreateClient();

    private Unit CreateTestUnit()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);
    }

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task<(Guid BookingId, string ManagementToken)> CreateGuestBookingAsync(int daysUntilCheckIn = 5)
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HttpResponseMessage holdResponse = await _client.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(3),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);
        HoldAvailabilityResponse? hold = await holdResponse.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        HttpResponseMessage confirmResponse = await _client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken);
        ConfirmBookingResponse? booking = await confirmResponse.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(booking?.ManagementToken);
        return (booking.BookingId, booking.ManagementToken);
    }

    private async Task<HttpResponseMessage> ExchangeAsync(Guid bookingId, string managementToken) =>
        await _client.PostAsJsonAsync($"/api/bookings/{bookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

    private async Task<string> SessionFor(Guid bookingId, string managementToken)
    {
        HttpResponseMessage response = await ExchangeAsync(bookingId, managementToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CreateBookingSessionResponse? session = await response.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(session?.SessionToken);
        return session.SessionToken;
    }

    private async Task<HttpResponseMessage> GetWithSessionAsync(Guid bookingId, string sessionToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"/api/bookings/{bookingId}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AValidManagementToken_ExchangesForASessionThatReachesTheBooking()
    {
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();

        string sessionToken = await SessionFor(bookingId, managementToken);

        // The point of the whole exercise: the management token is not in
        // this request at all, in the query string or anywhere else.
        HttpResponseMessage viewed = await GetWithSessionAsync(bookingId, sessionToken);

        Assert.Equal(HttpStatusCode.OK, viewed.StatusCode);
    }

    [Fact]
    public async Task ASessionForOneBooking_CannotReachAnother()
    {
        // The obvious attack, and a one-line mistake to leave open: the token
        // proves the bearer once held booking A's management token, which says
        // nothing about booking B.
        (Guid firstId, string firstToken) = await CreateGuestBookingAsync(daysUntilCheckIn: 5);
        (Guid secondId, _) = await CreateGuestBookingAsync(daysUntilCheckIn: 40);

        string sessionForFirst = await SessionFor(firstId, firstToken);

        HttpResponseMessage crossed = await GetWithSessionAsync(secondId, sessionForFirst);

        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }

    [Fact]
    public async Task ABookingSession_CannotAuthenticateAnOrdinaryEndpoint()
    {
        // The security boundary that makes this design safe at all. Both
        // tokens are signed with the same key by the same issuer, so if the
        // separation depended on a claim some handler had to remember to
        // check, this would be a privilege-escalation waiting to happen. It
        // depends on the audience instead, enforced inside the default bearer
        // scheme - so the principal is never built.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/bookings/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASession_CannotMintAnotherSession()
    {
        // Otherwise a 45-minute credential renews itself forever, and the
        // short lifetime - the only thing limiting a leaked session, since
        // there is no revocation inside the window - stops meaning anything.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        HttpRequestMessage request =
            new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/manage/session")
            {
                Content = JsonContent.Create(new CreateBookingSessionRequest
                {
                    BookingId = bookingId,
                    // A session token where the management token belongs.
                    ManagementToken = sessionToken
                })
            };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AWrongToken_IsNotDistinguishedFromAMissingBooking()
    {
        // Matches BookingAccessChecker's existing behaviour: "doesn't exist",
        // "isn't yours" and "expired" all answer identically, so the endpoint
        // cannot be used to enumerate bookings.
        (Guid bookingId, _) = await CreateGuestBookingAsync();

        HttpResponseMessage wrongToken = await ExchangeAsync(bookingId, "not-the-right-token-but-long-enough");
        HttpResponseMessage noSuchBooking = await ExchangeAsync(Guid.CreateVersion7(), "not-the-right-token-either");

        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noSuchBooking.StatusCode);
    }

    [Fact]
    public async Task TheManagementToken_SurvivesTheExchange()
    {
        // Not rotated and not consumed: the guest's link has to keep working
        // next week, which is the expectation an emailed link creates. The
        // short session is what limits exposure, not single use.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();

        await SessionFor(bookingId, managementToken);
        string second = await SessionFor(bookingId, managementToken);

        Assert.NotEmpty(second);
    }

    private async Task<HttpResponseMessage> CancelWithSessionAsync(
        Guid bookingId, string sessionToken, string? guestEmail)
    {
        HttpRequestMessage request =
            new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel")
            {
                Content = JsonContent.Create(new { bookingId, guestEmail })
            };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASession_CanCancelTheBooking_WithTheGuestEmail()
    {
        // Cancel takes the management token in its body today. The session has
        // to work there too, or the exchange only covers the read path and the
        // credential still travels for anything that matters.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        HttpResponseMessage response = await CancelWithSessionAsync(bookingId, sessionToken, "jane@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ALinkAlone_CannotCancel()
    {
        // The point of the second factor. Whoever forwarded, screenshotted or
        // shoulder-surfed the link can read the itinerary and no more.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        // Reading works.
        Assert.Equal(HttpStatusCode.OK, (await GetWithSessionAsync(bookingId, sessionToken)).StatusCode);

        HttpResponseMessage noEmail = await CancelWithSessionAsync(bookingId, sessionToken, guestEmail: null);
        HttpResponseMessage wrongEmail =
            await CancelWithSessionAsync(bookingId, sessionToken, "attacker@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, noEmail.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongEmail.StatusCode);

        // And nothing was cancelled by the attempt.
        using IServiceScope scope = factory.Services.CreateScope();
        Booking booking = await scope.ServiceProvider.GetRequiredService<BookingsDb>()
            .Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        Assert.NotEqual(BookingStatus.Cancelled, booking.BookingStatus);
    }

    [Fact]
    public async Task TheManagementViewStillRefusesToNameTheEmail()
    {
        // The whole second factor rests on this. If the management response
        // ever starts carrying the guest email, a link holder can read it and
        // cancel, and the field becomes theatre - so this asserts the
        // dependency rather than trusting it to stay true.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        HttpResponseMessage viewed = await GetWithSessionAsync(bookingId, sessionToken);
        string body = await viewed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("jane@example.com", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheEmailCheck_AcceptsADifferentCase()
    {
        // A confirmation step that rejects the right answer typed in the wrong
        // case teaches people the field is broken, not that it matters.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();
        string sessionToken = await SessionFor(bookingId, managementToken);

        HttpResponseMessage response =
            await CancelWithSessionAsync(bookingId, sessionToken, "  Jane@Example.COM  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheSessionResponse_ReportsItsOwnExpiry()
    {
        // So a client can re-exchange on a schedule instead of discovering
        // expiry as a 404 in the middle of a cancellation.
        (Guid bookingId, string managementToken) = await CreateGuestBookingAsync();

        HttpResponseMessage response = await ExchangeAsync(bookingId, managementToken);
        CreateBookingSessionResponse? session = await response.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(session.ExpiresAt < DateTimeOffset.UtcNow.AddHours(4));
    }
}
