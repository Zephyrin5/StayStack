using Bogus;
using Bookings;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Entities;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Bookings;

// Exercises CancelBookingEndpoint end-to-end - same "seed a Unit directly"
// shortcut GetMyBookingsTests uses, since ownership here is CustomerId, not
// anything Catalog-side.
[Collection("Integration Tests")]
public class CancelBookingTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private static Unit CreateTestUnit(decimal basePrice = 100m)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            basePrice);
    }

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.AddRange(entities);
        await context.SaveChangesAsync();
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

    private async Task<Guid> HoldUnitAsync(Guid unitId, DateOnly checkIn, DateOnly checkOut)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/catalog/holds", new HoldAvailabilityRequest
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

    private async Task<Guid> ConfirmBookingAsAsync(Guid holdId, string accessToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = _faker.Name.FullName(),
                GuestEmail = _faker.Internet.Email()
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.BookingId;
    }

    private async Task<ConfirmBookingResponse> ConfirmBookingAsGuestAsync(Guid holdId)
    {
        // No Authorization header - guest checkout, the only path that
        // gets a ManagementToken back.
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = _faker.Name.FullName(),
            GuestEmail = _faker.Internet.Email()
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    private async Task<HttpResponseMessage> CancelBookingAsync(Guid bookingId, string? accessToken, string? managementToken = null)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel")
        {
            // CancelBookingRequest carries a real body-bindable field
            // (ManagementToken) now, not just the route-bound BookingId -
            // the endpoint expects a JSON body the same as any other POST
            // request with fields, matching how a real client (openapi-fetch)
            // always sends one.
            Content = JsonContent.Create(new CancelBookingRequest { BookingId = bookingId, ManagementToken = managementToken })
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
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

    private async Task<Guid> InitiateTransactionAsync(Guid bookingId)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        InitiateTransactionResponse? result =
            await response.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.TransactionId;
    }

    private async Task MarkTransactionSucceededAsync(Guid transactionId, string adminToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/transactions/{transactionId}/succeed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<TransactionStatus> GetTransactionStatusAsync(Guid transactionId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppTransactionsDbContext context = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        Transaction transaction = await context.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        return transaction.TransactionStatus;
    }

    [Fact]
    public async Task CancelBooking_ShouldMoveSucceededTransactionToRefundPending()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        string adminToken = await SignInAsAdministratorAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);
        Guid transactionId = await InitiateTransactionAsync(bookingId);
        await MarkTransactionSucceededAsync(transactionId, adminToken);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(bookingId, customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TransactionStatus.RefundPending, await GetTransactionStatusAsync(transactionId));
    }

    [Fact]
    public async Task CancelBooking_ShouldLeaveAPendingTransactionUntouched()
    {
        // Arrange - a still-Pending transaction's eventual outcome isn't
        // known yet at cancel time, so it's left alone rather than guessed
        // at. See MarkTransactionSucceededAgainstAlreadyCancelledBooking_
        // ShouldMoveTheTransactionStraightToRefundPending below for how a
        // late success against this same booking is handled instead.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);
        Guid transactionId = await InitiateTransactionAsync(bookingId);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(bookingId, customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TransactionStatus.Pending, await GetTransactionStatusAsync(transactionId));
    }

    [Fact]
    public async Task MarkTransactionSucceededAgainstAlreadyCancelledBooking_ShouldMoveTheTransactionStraightToRefundPending()
    {
        // Arrange - simulates a payment that was still in flight at the
        // gateway when the customer cancelled, and only resolves
        // afterward. The webhook (here, the admin stand-in) is reporting a
        // fact that already happened externally - it can't just be
        // rejected because the booking moved on, so this has to start a
        // refund instead of erroring.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        string adminToken = await SignInAsAdministratorAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);
        Guid transactionId = await InitiateTransactionAsync(bookingId);

        HttpResponseMessage cancelResponse = await CancelBookingAsync(bookingId, customerToken);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Act
        await MarkTransactionSucceededAsync(transactionId, adminToken);

        // Assert
        Assert.Equal(TransactionStatus.RefundPending, await GetTransactionStatusAsync(transactionId));

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        Assert.Equal(BookingStatus.Cancelled, booking.BookingStatus);
    }

    [Fact]
    public async Task CancelBooking_ShouldSetStatusToCancelled_AndReleaseTheHoldImmediately()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(bookingId, customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CancelBookingResponse? result = await response.Content.ReadFromJsonAsync<CancelBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(bookingId, result.BookingId);
        Assert.Equal(BookingStatus.Cancelled, result.BookingStatus);

        // The hold's own 15-minute window is still wide open at this point -
        // this only succeeds if ReleaseHoldAsync actually reset
        // hold_expires_at rather than leaving the range blocked until the
        // original timer ran out.
        Guid secondHoldId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Assert.NotEqual(Guid.Empty, secondHoldId);
    }

    [Fact]
    public async Task CancelBooking_ShouldBeIdempotent_WhenCalledTwice()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, customerToken);
        await CancelBookingAsync(bookingId, customerToken);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(bookingId, customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CancelBookingResponse? result = await response.Content.ReadFromJsonAsync<CancelBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(BookingStatus.Cancelled, result.BookingStatus);
    }

    [Fact]
    public async Task CancelBooking_ShouldReturn404_WhenBookingBelongsToAnotherCustomer()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string ownerToken = await SeedSignedInCustomerAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        Guid bookingId = await ConfirmBookingAsAsync(holdId, ownerToken);

        string otherCustomerToken = await SeedSignedInCustomerAsync();

        // Act
        HttpResponseMessage response = await CancelBookingAsync(bookingId, otherCustomerToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_ShouldReturn404_WhenBookingDoesNotExist()
    {
        // Arrange
        string customerToken = await SeedSignedInCustomerAsync();

        // Act
        HttpResponseMessage response = await CancelBookingAsync(Guid.NewGuid(), customerToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_ShouldReturn404_WhenNotAuthenticatedAndNoToken()
    {
        // The endpoint is public (AllowAnonymous) now - a guest-checkout
        // caller with no account has to be able to reach it at all, using a
        // ManagementToken instead of a session. An anonymous caller with
        // neither an account nor a token gets the same 404 every other
        // ownership mismatch gets, not 401 - see CancelBookingEndpoint's
        // own doc comment for why this changed from the old
        // authentication-required behavior.
        HttpResponseMessage response = await CancelBookingAsync(Guid.NewGuid(), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelBooking_ShouldSucceed_ForGuestCheckoutWithCorrectManagementToken()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        ConfirmBookingResponse booking = await ConfirmBookingAsGuestAsync(holdId);
        Assert.NotNull(booking.ManagementToken);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(booking.BookingId, accessToken: null, booking.ManagementToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CancelBookingResponse? result = await response.Content.ReadFromJsonAsync<CancelBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(BookingStatus.Cancelled, result.BookingStatus);
    }

    [Fact]
    public async Task CancelBooking_ShouldReturn404_ForGuestCheckoutWithWrongManagementToken()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));
        ConfirmBookingResponse booking = await ConfirmBookingAsGuestAsync(holdId);

        // Act
        HttpResponseMessage response = await CancelBookingAsync(booking.BookingId, accessToken: null, managementToken: "not-the-real-token");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldNotReturnManagementToken_ForAnAuthenticatedCustomer()
    {
        // An authenticated caller's own session is already proof of
        // ownership - issuing a token nobody will ever need would just be a
        // second, redundant way to access the same booking.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SeedSignedInCustomerAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid holdId = await HoldUnitAsync(unit.Id, today, today.AddDays(3));

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = holdId,
                GuestName = _faker.Name.FullName(),
                GuestEmail = _faker.Internet.Email()
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Null(result.ManagementToken);
    }
}
