using Bogus;
using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Availability.Features.HoldAvailability;
using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promotions;
using Promotions.Entities;
using Promotions.Enums;
using Promotions.Features.CreatePromotion;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Promotions;

// Exercises the full promo-code redemption flow end-to-end over real HTTP:
// hold -> confirm with a code -> discounted total, plus the invariants that
// make a coupon safe to offer at all (one use per guest email, a
// redemption cap, expiry, host-scoping, currency-matching, and the
// LOS-discount-exclusivity rule from ConfirmBookingHandler). Same
// real-HTTP-over-HttpClient approach as ConfirmBookingTests/
// PricingRuleHandlerTests, combined into one flow here since redemption is
// inherently a cross-feature (holds + pricing rules + promotions +
// bookings) concern.
[Collection("Integration Tests")]
public class PromotionRedemptionTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SignInAsSeededAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<(Guid HostId, string AccessToken)> SeedHostUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

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

        return (becomeHostResult.HostId, becomeHostResult.AccessToken);
    }

    private async Task<string> SeedSignedInCustomerAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);
        return signInResult.AccessToken;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken, object? body = null)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<Guid> CreatePropertyAsync(string hostAccessToken)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/properties", hostAccessToken, new CreatePropertyRequest
            {
                TimeZoneId = "Asia/Kuwait",
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Test Property" } },
                City = "Kuwait City"
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? result = await response.Content.ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(string hostAccessToken, Guid propertyId, decimal basePrice = 100m, Currency currency = Currency.KWD)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/units", hostAccessToken, new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Test Unit" } },
                MaxOccupancy = 4,
                BasePrice = basePrice,
                Currency = currency
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? result = await response.Content.ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.UnitId;
    }

    private async Task<(Guid HostId, string HostToken, Guid UnitId)> SeedHostWithUnitAsync(
        decimal basePrice = 100m, Currency currency = Currency.KWD)
    {
        (Guid hostId, string hostToken) = await SeedHostUserAsync();
        Guid propertyId = await CreatePropertyAsync(hostToken);
        Guid unitId = await CreateUnitAsync(hostToken, propertyId, basePrice, currency);
        return (hostId, hostToken, unitId);
    }

    private async Task CreateLengthOfStayDiscountRuleAsync(string hostToken, Guid unitId, int minNights, decimal discountPercent)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, $"/api/catalog/units/{unitId}/pricing-rules", hostToken, new CreatePricingRuleRequest
            {
                UnitId = unitId,
                RuleType = PricingRuleType.LengthOfStayDiscount,
                MinNights = minNights,
                DiscountPercent = discountPercent
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreatePromotionAsync(
        string accessToken, string? code = null, PromotionDiscountType discountType = PromotionDiscountType.Percentage,
        decimal discountValue = 20m, Currency? currency = null, int? maxRedemptions = null, DateTimeOffset? expiresAt = null)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/promotions", accessToken, new CreatePromotionRequest
            {
                Code = code ?? _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = discountType,
                DiscountValue = discountValue,
                Currency = currency,
                MaxRedemptions = maxRedemptions,
                ExpiresAt = expiresAt
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePromotionResponse? result = await response.Content.ReadFromJsonAsync<CreatePromotionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.PromotionId;
    }

    private async Task<Guid> HoldUnitAsync(Guid unitId, DateOnly checkIn, DateOnly checkOut, HttpClient? client = null)
    {
        HttpResponseMessage response = await (client ?? _client).PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = checkIn,
            CheckOut = checkOut,
            GuestCount = 2
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HoldAvailabilityResponse? hold = await response.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private async Task<HttpResponseMessage> ConfirmBookingRawAsync(
        Guid holdId, string guestEmail, string? promoCode, string? accessToken = null, HttpClient? client = null)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = _faker.Name.FullName(),
                GuestEmail = guestEmail,
                PromoCode = promoCode
            })
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await (client ?? _client).SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<ConfirmBookingResponse> ConfirmBookingAsync(
        Guid holdId, string guestEmail, string? promoCode, string? accessToken = null)
    {
        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, guestEmail, promoCode, accessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public async Task ConfirmBooking_ShouldApplyPercentageDiscount_WhenValidCodeRedeemed()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m);
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        Guid promotionId = await CreatePromotionAsync(hostToken, code, PromotionDiscountType.Percentage, 20m);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3)); // 3 nights * 100 = 300

        ConfirmBookingResponse result = await ConfirmBookingAsync(holdId, "guest@example.com", code);

        Assert.Equal(240m, result.TotalPrice); // 300 - 20%

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);
        Assert.Equal(1, promotion.RedemptionCount);
        PromotionRedemption redemption = await db.PromotionRedemptions
            .SingleAsync(r => r.PromotionId == promotionId, TestContext.Current.CancellationToken);
        Assert.Equal(60m, redemption.DiscountAmount.Amount);
        Assert.Equal("guest@example.com", redemption.GuestEmail);
        Assert.Equal(result.BookingId, redemption.BookingId);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldApplyFixedAmountDiscount_WhenValidCodeRedeemed()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m, Currency.KWD);
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, PromotionDiscountType.FixedAmount, 50m, Currency.KWD);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3)); // 300

        ConfirmBookingResponse result = await ConfirmBookingAsync(holdId, "guest@example.com", code);

        Assert.Equal(250m, result.TotalPrice);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenCodeDoesNotExist()
    {
        (_, _, Guid unitId) = await SeedHostWithUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3));

        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, "guest@example.com", "NOSUCHCODE");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The error key, not just the status. ConfirmBookingHandler passes a
        // bare nameof(request.PromoCode) - PascalCase - and relies on
        // GlobalExceptionHandler.BuildValidationProblem to camelCase it on the
        // way out, so this is what proves that central conversion actually
        // runs for a handler-thrown ValidationException. It previously
        // converted the key at the throw site instead, and nothing asserted
        // the result either way.
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"promoCode\"", body);
        Assert.DoesNotContain("PromoCode", body);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenCodeExpired()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3));

        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, "guest@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenCodeAlreadyUsedByThisEmail()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid firstHoldId = await HoldUnitAsync(unitId, today, today.AddDays(2));
        await ConfirmBookingAsync(firstHoldId, "repeat@example.com", code);

        // A second, non-overlapping hold on the same unit so the only thing
        // under test is the code+email reuse, not the double-booking guard.
        Guid secondHoldId = await HoldUnitAsync(unitId, today.AddDays(10), today.AddDays(12));

        HttpResponseMessage response = await ConfirmBookingRawAsync(secondHoldId, "repeat@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenRedemptionCapReached()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, maxRedemptions: 1);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid firstHoldId = await HoldUnitAsync(unitId, today, today.AddDays(2));
        await ConfirmBookingAsync(firstHoldId, "first@example.com", code);

        Guid secondHoldId = await HoldUnitAsync(unitId, today.AddDays(10), today.AddDays(12));

        HttpResponseMessage response = await ConfirmBookingRawAsync(secondHoldId, "second@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenCodeBelongsToADifferentHost()
    {
        (_, string ownerToken, _) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(ownerToken, code);

        (_, _, Guid otherUnitId) = await SeedHostWithUnitAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(otherUnitId, today, today.AddDays(3));

        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, "guest@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn400_WhenFixedAmountCurrencyDoesNotMatchUnit()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m, Currency.KWD);
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, PromotionDiscountType.FixedAmount, 10m, Currency.USD);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3));

        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, "guest@example.com", code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_CouponShouldReplaceLengthOfStayDiscount_NotStackWithIt()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m);
        await CreateLengthOfStayDiscountRuleAsync(hostToken, unitId, minNights: 3, discountPercent: 10m);
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, PromotionDiscountType.Percentage, 20m);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        // 3 nights * 100 = 300 subtotal; LOS discount alone would give 270.
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3));

        ConfirmBookingResponse result = await ConfirmBookingAsync(holdId, "guest@example.com", code);

        // Exclusive, not stacked: coupon applies to the 300 subtotal
        // (LOS discount undone), not to the already-270 LOS-discounted
        // total - 300 * 0.8 = 240, not 270 * 0.8 = 216.
        Assert.Equal(240m, result.TotalPrice);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldNotApplyLengthOfStayDiscount_WhenNoCodeRedeemed()
    {
        // Control for the exclusivity test above - without a code, the LOS
        // discount still applies normally.
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m);
        await CreateLengthOfStayDiscountRuleAsync(hostToken, unitId, minNights: 3, discountPercent: 10m);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unitId, today, today.AddDays(3));

        ConfirmBookingResponse result = await ConfirmBookingAsync(holdId, "guest@example.com", null);

        Assert.Equal(270m, result.TotalPrice);
    }

    [Fact]
    public async Task CancelBooking_ShouldReverseRedemption_AllowingCodeReuse()
    {
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        Guid promotionId = await CreatePromotionAsync(hostToken, code, maxRedemptions: 1);
        string customerToken = await SeedSignedInCustomerAsync();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid firstHoldId = await HoldUnitAsync(unitId, today, today.AddDays(2));
        ConfirmBookingResponse firstBooking = await ConfirmBookingAsync(firstHoldId, "guest@example.com", code, customerToken);

        using HttpRequestMessage cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{firstBooking.BookingId}/cancel")
        {
            Content = JsonContent.Create(new CancelBookingRequest { BookingId = firstBooking.BookingId })
        };
        cancelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        HttpResponseMessage cancelResponse = await _client.SendAsync(cancelRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotionAfterCancel = await db.Promotions.SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);
        Assert.Equal(0, promotionAfterCancel.RedemptionCount);

        // The cap was 1 and is now given back - a fresh hold, same email,
        // same code should succeed again.
        Guid secondHoldId = await HoldUnitAsync(unitId, today.AddDays(10), today.AddDays(12));
        ConfirmBookingResponse secondBooking = await ConfirmBookingAsync(secondHoldId, "guest@example.com", code);

        Assert.NotEqual(firstBooking.BookingId, secondBooking.BookingId);
    }

    [Fact]
    public async Task Redeem_ConcurrentRequestsForSameCodeWithMaxRedemptionsOfOne_ExactlyOneSucceeds()
    {
        // This is the highest-value concurrency test for promotions,
        // mirroring HoldAvailabilityConcurrencyTests' reasoning: proves the
        // atomic conditional UPDATE on promotions.redemption_count - not
        // application code - is what actually makes "at most one redemption
        // when MaxRedemptions=1" hold under a real race, not just a
        // single-threaded check-then-act that happens to look correct.
        // Holds are pre-created sequentially (non-overlapping date ranges,
        // one per attempt) so the only contention under test is the
        // redemption cap itself, not the unrelated double-booking guard.
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        Guid promotionId = await CreatePromotionAsync(hostToken, code, maxRedemptions: 1);

        const int concurrentRequests = 8;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid[] holdIds = new Guid[concurrentRequests];
        for (int i = 0; i < concurrentRequests; i++)
        {
            // A fresh client per iteration, same as the confirm step below -
            // these represent 8 different prospective guests, not one
            // session holding 8 ranges, so each needs its own hold-session
            // cookie rather than sharing _client's.
            holdIds[i] = await HoldUnitAsync(unitId, today.AddDays(i * 3), today.AddDays(i * 3 + 2), factory.CreateClient());
        }

        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(i => ConfirmBookingRawAsync(holdIds[i], $"guest{i}@example.com", code, client: factory.CreateClient()))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.AsNoTracking()
            .SingleAsync(p => p.Id == promotionId, TestContext.Current.CancellationToken);
        Assert.Equal(1, promotion.RedemptionCount);
        int redemptionRowCount = await db.PromotionRedemptions
            .CountAsync(r => r.PromotionId == promotionId, TestContext.Current.CancellationToken);
        Assert.Equal(1, redemptionRowCount);
    }

    [Fact]
    public async Task ConfirmBooking_WithAFullyDiscountingCode_IsRejected_NotTurnedIntoAnUnpayableBooking()
    {
        // A 100% code drives the total to exactly zero, and Promotion
        // explicitly permits one (see PromotionTests'
        // CreateHostPromotion_ShouldAllowPercentageDiscountValueOfExactlyOneHundred).
        // ComputeDiscountAmount caps a FixedAmount discount at the subtotal
        // too, so a large enough fixed code lands in the same place.
        //
        // The guard here is "discountedPrice >= hold.TotalPrice", and
        // 0 >= 300 is false - so a zero-total Booking used to be created
        // happily. Transaction.Create then refuses it
        // (Guard.Against.NegativeOrZero), leaving the guest holding a booking
        // they can never pay for: stuck Pending forever, with a raw
        // guard-clause message surfacing on every payment attempt. Rejected
        // at checkout now, with Booking.Create enforcing the same invariant
        // so no other path can reintroduce it.
        (_, string hostToken, Guid unitId) = await SeedHostWithUnitAsync(100m);
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreatePromotionAsync(hostToken, code, PromotionDiscountType.Percentage, 100m);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(200);
        Guid holdId = await HoldUnitAsync(unitId, checkIn, checkIn.AddDays(3));

        HttpResponseMessage response = await ConfirmBookingRawAsync(holdId, _faker.Internet.Email(), code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
