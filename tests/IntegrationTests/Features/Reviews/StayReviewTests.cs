using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using BuildingBlocks.Policies;
using Bogus;
using Bookings;
using Bookings.Entities;
using BuildingBlocks.Security;
using Catalog;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reviews;
using Reviews.Entities;
using Reviews.Features.CreateStayReview;
using Reviews.Features.GetPropertyReviews;
using Reviews.Features.ListMyReviewableBookings;
using Reviews.Features.ReplyToStayReview;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Reviews;

// Exercises the guest-facing stay-review slices end-to-end. A real
// Property/Unit is created through the actual host endpoints (not a
// directly-seeded Unit) - Reviews resolves PropertyId/HostId via
// Catalog.Contracts.IUnitLookup, which only returns a real HostId when the
// unit's PropertyId points at an actual Property row (see UnitLookup's own
// LEFT JOIN comment). The Booking itself is seeded directly into
// AppBookingsDbContext with a Confirmed status and an explicit CheckOut date
// - going through the real Hold/Confirm HTTP flow can't produce a
// checkout-already-passed booking, since HoldAvailabilityEndpoint requires a
// future date range.
[Collection("Integration Tests")]
public class StayReviewTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    // Read from the running host rather than hardcoded: these tests assert
    // boundaries relative to the configured window, so a deployment that
    // changes it should still be exercised at its own edges.
    private int ReviewWindowDays =>
        factory.Services.GetRequiredService<IOptions<BookingLifecyclePolicyOptions>>()
            .Value.ReviewWindowDaysAfterCheckOut;

    private async Task<string> SeedHostUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test host user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        using HttpRequestMessage becomeHostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(new BecomeHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email()
            })
        };
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        return becomeHostResult.AccessToken;
    }

    private async Task<(Guid CustomerId, string AccessToken)> SeedSignedInCustomerAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test customer user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);
        return (signInResult.Id, signInResult.AccessToken);
    }

    private async Task<string> SignInAsAdministratorAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SignInResponse? result =
            await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<Guid> CreatePropertyAsync(string hostAccessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/properties")
        {
            Content = JsonContent.Create(new CreatePropertyRequest
            {
                TimeZoneId = "Asia/Kuwait",
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Seaside Hotel" } },
                City = "Kuwait City"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAccessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(Guid propertyId, string hostAccessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units")
        {
            Content = JsonContent.Create(new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Standard Room" } },
                MaxOccupancy = 2,
                BasePrice = 100m
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAccessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    [Fact]
    public void Startup_WithATokenLifetimeShorterThanTheReviewWindow_RefusesToStart()
    {
        // The two settings are independent so each can be chosen on its own
        // merits, but they are not unrelated: a management token that dies
        // before the review window closes puts guest checkout back exactly
        // where it started - reviewable in principle, locked out of its own
        // booking in practice - so the window would quietly apply to
        // signed-in customers only.
        //
        // That is invisible in production: the symptom is guests not
        // reviewing. Program.cs refuses to start instead, and this asserts the
        // throw is reachable rather than merely described.
        using WebApplicationFactory<Program> misconfigured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<BookingLifecyclePolicyOptions>(o =>
            {
                o.ReviewWindowDaysAfterCheckOut = 120;
                o.ManagementTokenLifetimeDaysAfterCheckOut = 90;
            })));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => misconfigured.CreateClient());

        Assert.Contains("shorter than", exception.Message);
    }

    [Fact]
    public async Task CreateStayReview_ForAStayPastTheReviewWindow_IsRejected_ForAnAuthenticatedCustomerToo()
    {
        // Reviews had only a lower bound ("has the stay ended"), so the
        // effective deadline came from the guest management token's lifetime -
        // which meant it applied to guest checkout only. An authenticated
        // customer could review the same stay forever, and two people in the
        // same room on the same night had different rights depending on
        // whether they had an account.
        //
        // The window is now explicit and applies to both. This is the path
        // that had no deadline at all before.
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();

        DateOnly today = CatalogSeeding.Today();
        DateOnly checkOut = today.AddDays(-(ReviewWindowDays + 5));
        Guid bookingId = await SeedBookingAsync(unitId, customerId, checkOut.AddDays(-3), checkOut);

        HttpResponseMessage response = await CreateStayReviewAsync(
            new CreateStayReviewRequest
            {
                BookingId = bookingId,
                CleanlinessRating = 5,
                CommunicationRating = 5,
                LocationRating = 5,
                ValueRating = 5,
                AccuracyRating = 5,
                Comment = "Lovely stay."
            },
            customerToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "Reviews close",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateStayReview_OnTheLastDayOfTheWindow_IsStillAccepted()
    {
        // The boundary itself, inclusive - a review exactly ReviewWindowDays
        // after checkout still lands. Pins which side of the comparison the
        // deadline falls on, which an off-by-one would otherwise move
        // silently.
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();

        DateOnly today = CatalogSeeding.Today();
        DateOnly checkOut = today.AddDays(-ReviewWindowDays);
        Guid bookingId = await SeedBookingAsync(unitId, customerId, checkOut.AddDays(-3), checkOut);

        HttpResponseMessage response = await CreateStayReviewAsync(
            new CreateStayReviewRequest
            {
                BookingId = bookingId,
                CleanlinessRating = 5,
                CommunicationRating = 5,
                LocationRating = 5,
                ValueRating = 5,
                AccuracyRating = 5,
                Comment = "Lovely stay."
            },
            customerToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListMyReviewableBookings_OmitsStaysPastTheReviewWindow()
    {
        // The list must not offer what CreateStayReview would then reject -
        // the same "UI agrees with the API" rule the lower bound already had,
        // which an upper bound on only one side would have broken at the far
        // end.
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();

        DateOnly today = CatalogSeeding.Today();

        DateOnly expiredCheckOut = today.AddDays(-(ReviewWindowDays + 5));
        Guid expiredBookingId = await SeedBookingAsync(unitId, customerId, expiredCheckOut.AddDays(-3), expiredCheckOut);

        DateOnly recentCheckOut = today.AddDays(-2);
        Guid recentBookingId = await SeedBookingAsync(unitId, customerId, recentCheckOut.AddDays(-3), recentCheckOut);

        HttpResponseMessage response = await ListMyReviewableBookingsAsync(customerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(recentBookingId.ToString(), body);
        Assert.DoesNotContain(expiredBookingId.ToString(), body);
    }

    private async Task<Guid> SeedBookingAsync(
        Guid unitId, Guid? customerId, DateOnly checkIn, DateOnly checkOut, bool confirmed = true)
    {
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), unitId, Guid.NewGuid(), customerId,
            _faker.Name.FullName(), _faker.Internet.Email(), null, checkIn, checkOut, 2, Money.Of(300m, Currency.KWD), Money.Of(300m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait");
        if (confirmed)
        {
            booking.Confirm();
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        context.Bookings.Add(booking);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return booking.Id;
    }

    private async Task<string> SeedManagementTokenAsync(Guid bookingId)
    {
        string rawToken = SecureToken.Generate();

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        context.BookingManagementTokens.Add(new BookingManagementToken
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            TokenHash = SecureToken.Hash(rawToken),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return rawToken;
    }

    private static CreateStayReviewRequest CreateValidReviewRequest(Guid bookingId, string? managementToken = null) =>
        new CreateStayReviewRequest
        {
            BookingId = bookingId,
            ManagementToken = managementToken,
            CleanlinessRating = 5,
            CommunicationRating = 4,
            LocationRating = 3,
            ValueRating = 2,
            AccuracyRating = 1,
            Comment = "Lovely stay"
        };

    private async Task<HttpResponseMessage> CreateStayReviewAsync(CreateStayReviewRequest request, string? accessToken = null)
    {
        using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/reviews/stays")
        {
            Content = JsonContent.Create(request)
        };
        if (accessToken is not null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client.SendAsync(httpRequest, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> GetPropertyReviewsAsync(Guid propertyId) =>
        await _client.GetAsync($"/api/reviews/stays/property/{propertyId}", TestContext.Current.CancellationToken);

    private async Task<HttpResponseMessage> ListMyReviewableBookingsAsync(string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/reviews/stays/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ReplyToStayReviewAsync(Guid stayReviewId, string replyText, string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/reviews/stays/{stayReviewId}/reply")
        {
            Content = JsonContent.Create(new ReplyToStayReviewRequest { StayReviewId = stayReviewId, ReplyText = replyText })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> DeleteStayReviewAsync(Guid stayReviewId, string adminToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"/api/reviews/stays/{stayReviewId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateStayReview_ShouldSucceed_ForAuthenticatedCustomerWithConfirmedPastBooking()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateStayReviewResponse? result = await response.Content.ReadFromJsonAsync<CreateStayReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.StayReviewId);
    }

    [Fact]
    public async Task CreateStayReview_ShouldSucceed_ForGuestCheckoutWithManagementToken()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, null, today.AddDays(-5), today.AddDays(-2));
        string managementToken = await SeedManagementTokenAsync(bookingId);

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId, managementToken));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateStayReview_ShouldReturn404_ForGuestCheckoutWithWrongManagementToken()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, null, today.AddDays(-5), today.AddDays(-2));
        await SeedManagementTokenAsync(bookingId);

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId, "not-the-real-token"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateStayReview_ShouldReturn400_WhenBookingHasNotBeenConfirmedYet()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2), confirmed: false);

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateStayReview_ShouldReturn400_WhenCheckoutHasNotPassedYet()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today, today.AddDays(3));

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateStayReview_ShouldReturn409_WhenTheBookingHasAlreadyBeenReviewed()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Act
        HttpResponseMessage response = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetPropertyReviews_ShouldReturnTheReviewAndAMatchingRatingSummary()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);

        // Act
        HttpResponseMessage response = await GetPropertyReviewsAsync(propertyId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GetPropertyReviewsResponse? result = await response.Content.ReadFromJsonAsync<GetPropertyReviewsResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        StayReviewSummary review = Assert.Single(result.Reviews.Items);
        Assert.Equal("Lovely stay", review.Comment);
        Assert.Equal(3m, review.OverallRating); // (5+4+3+2+1)/5
        Assert.Equal(1, result.RatingSummary.Count);
        Assert.Equal(3m, result.RatingSummary.AverageOverall);
        Assert.Equal(5m, result.RatingSummary.AverageCleanliness);
    }

    [Fact]
    public async Task ListMyReviewableBookings_ShouldOnlyReturnConfirmedPastNotYetReviewedBookings()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();

        Guid reviewableBookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        Guid alreadyReviewedBookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-10), today.AddDays(-7));
        await CreateStayReviewAsync(CreateValidReviewRequest(alreadyReviewedBookingId), customerToken);
        await SeedBookingAsync(unitId, customerId, today, today.AddDays(3)); // not yet checked out

        // Act
        HttpResponseMessage response = await ListMyReviewableBookingsAsync(customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListMyReviewableBookingsResponse? result = await response.Content.ReadFromJsonAsync<ListMyReviewableBookingsResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        ReviewableBookingSummary booking = Assert.Single(result.Bookings);
        Assert.Equal(reviewableBookingId, booking.BookingId);
    }

    [Fact]
    public async Task ReplyToStayReview_ShouldSucceed_ForTheOwningHost()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        HttpResponseMessage createResponse = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);
        CreateStayReviewResponse? created = await createResponse.Content.ReadFromJsonAsync<CreateStayReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        // Act
        HttpResponseMessage response = await ReplyToStayReviewAsync(created.StayReviewId, "Thanks for staying with us!", hostToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HttpResponseMessage propertyReviewsResponse = await GetPropertyReviewsAsync(propertyId);
        GetPropertyReviewsResponse? propertyReviews = await propertyReviewsResponse.Content.ReadFromJsonAsync<GetPropertyReviewsResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(propertyReviews);
        Assert.Equal("Thanks for staying with us!", Assert.Single(propertyReviews.Reviews.Items).HostReplyText);
    }

    [Fact]
    public async Task ReplyToStayReview_ShouldReturn404_ForANonOwningHost()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        HttpResponseMessage createResponse = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);
        CreateStayReviewResponse? created = await createResponse.Content.ReadFromJsonAsync<CreateStayReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        string otherHostToken = await SeedHostUserAsync();

        // Act
        HttpResponseMessage response = await ReplyToStayReviewAsync(created.StayReviewId, "Not my property", otherHostToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteStayReview_ShouldRemoveTheReviewFromThePropertysPublicList()
    {
        // Arrange
        string hostToken = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(propertyId, hostToken);
        (Guid customerId, string customerToken) = await SeedSignedInCustomerAsync();
        DateOnly today = CatalogSeeding.Today();
        Guid bookingId = await SeedBookingAsync(unitId, customerId, today.AddDays(-5), today.AddDays(-2));
        HttpResponseMessage createResponse = await CreateStayReviewAsync(CreateValidReviewRequest(bookingId), customerToken);
        CreateStayReviewResponse? created = await createResponse.Content.ReadFromJsonAsync<CreateStayReviewResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        string adminToken = await SignInAsAdministratorAsync();

        // Act
        HttpResponseMessage response = await DeleteStayReviewAsync(created.StayReviewId, adminToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HttpResponseMessage propertyReviewsResponse = await GetPropertyReviewsAsync(propertyId);
        GetPropertyReviewsResponse? propertyReviews = await propertyReviewsResponse.Content.ReadFromJsonAsync<GetPropertyReviewsResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(propertyReviews);
        Assert.Empty(propertyReviews.Reviews.Items);
        Assert.Equal(0, propertyReviews.RatingSummary.Count);

        using IServiceScope scope = factory.Services.CreateScope();
        AppReviewsDbContext reviewsDb = scope.ServiceProvider.GetRequiredService<AppReviewsDbContext>();
        StayReview archived = await reviewsDb.StayReviews.IgnoreQueryFilters()
            .SingleAsync(r => r.Id == created.StayReviewId, TestContext.Current.CancellationToken);
        Assert.Equal(EntityStatus.Archived, archived.Status);
    }
}
