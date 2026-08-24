using Bogus;
using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using BuildingBlocks.Pagination;
using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Entities;
using Transactions.Features.GetTransactions;
using Transactions.Features.InitiateTransaction;
using Transactions.Features.MarkTransactionFailed;
namespace IntegrationTests.Features.Transactions;

// Exercises the full hold -> confirm -> initiate transaction -> succeed
// path end-to-end over real HTTP, following ConfirmBookingTests.cs's
// seed/hold/confirm pattern.
[Collection("Integration Tests")]
public class TransactionsTests(IntegrationTestWebApplicationFactory factory)
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

    private async Task<Guid> HoldAndConfirmBookingAsync(Guid unitId)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        HttpResponseMessage holdResponse = await _client.PostAsJsonAsync("/api/catalog/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, holdResponse.StatusCode);
        HoldAvailabilityResponse? hold =
            await holdResponse.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        HttpResponseMessage confirmResponse = await _client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        ConfirmBookingResponse? confirmed =
            await confirmResponse.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(confirmed);
        return confirmed.BookingId;
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

    private async Task<string> SignInAsNonAdministratorAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? result =
            await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private static HttpRequestMessage AuthorizedPost(string path, object? body, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthorizedGet(string path, string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task InitiateThenSucceed_ShouldConfirmTheBooking()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        // Act - initiate
        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);

        // Assert - initiate
        Assert.Equal(HttpStatusCode.OK, initiateResponse.StatusCode);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);
        Assert.Equal(300m, initiated.Amount); // 100/night * 3 nights
        Assert.Equal(Currency.KWD, initiated.Currency);

        // Act - succeed
        HttpResponseMessage succeedResponse = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{initiated.TransactionId}/succeed", null, adminToken),
            TestContext.Current.CancellationToken);

        // Assert - succeed
        Assert.Equal(HttpStatusCode.OK, succeedResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        Assert.Equal(BookingStatus.Confirmed, booking.BookingStatus);
    }

    [Fact]
    public async Task Initiate_ShouldReturn409_WhenATransactionIsAlreadyInProgress()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);

        HttpResponseMessage firstResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act
        HttpResponseMessage secondResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Initiate_ConcurrentRequestsForSameBooking_ExactlyOneTransactionIsCreated()
    {
        // The pre-check (AnyAsync) alone can't stop two concurrent requests
        // from both observing "no transaction yet" and both inserting -
        // the partial unique index on (booking_id) WHERE status IN
        // (Pending, Succeeded) is the actual authority, surfaced back as
        // TransactionAlreadyInProgressException via the DbUpdateException
        // catch in InitiateTransactionHandler.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);

        // Act: fire concurrent InitiateTransaction requests for the same booking.
        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks = [.. Enumerable.Range(0, concurrentRequests)
            .Select(_ => factory.CreateClient().PostAsJsonAsync(
                "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken))];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(concurrentRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using IServiceScope scope = factory.Services.CreateScope();
        AppTransactionsDbContext transactionsDb = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        int transactionCount = await transactionsDb.Transactions.CountAsync(t => t.BookingId == bookingId, TestContext.Current.CancellationToken);
        Assert.Equal(1, transactionCount);
    }

    [Fact]
    public async Task Initiate_ShouldReturn404_WhenBookingDoesNotExist()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Succeed_ShouldReturn403_ForNonAdministratorCaller()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);
        string nonAdminToken = await SignInAsNonAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        // Act
        HttpResponseMessage succeedResponse = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{initiated.TransactionId}/succeed", null, nonAdminToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, succeedResponse.StatusCode);
    }

    [Fact]
    public async Task FailThenRetry_ShouldAllowANewTransaction()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        // Act - fail the first transaction
        HttpResponseMessage failResponse = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{initiated.TransactionId}/fail",
                new MarkTransactionFailedRequest { Reason = "Card declined" }, adminToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, failResponse.StatusCode);

        // Act - retry
        HttpResponseMessage retryResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        Assert.Equal(BookingStatus.Pending, booking.BookingStatus);
    }

    [Fact]
    public async Task GetTransactions_ShouldReturnCreatedTransaction_AndSupportStatusFilter_ForAdministrator()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        // Act - unfiltered
        HttpResponseMessage allResponse = await _client.SendAsync(
            AuthorizedGet("/api/transactions", adminToken), TestContext.Current.CancellationToken);

        // Assert - unfiltered
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        PagedResponse<TransactionSummary>? all =
            await allResponse.Content.ReadFromJsonAsync<PagedResponse<TransactionSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(all);
        Assert.Contains(all.Items, t => t.TransactionId == initiated.TransactionId);

        // Act - filtered to a status the seeded transaction doesn't have
        HttpResponseMessage filteredResponse = await _client.SendAsync(
            AuthorizedGet("/api/transactions?Status=Succeeded", adminToken), TestContext.Current.CancellationToken);

        // Assert - filtered
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        PagedResponse<TransactionSummary>? filtered =
            await filteredResponse.Content.ReadFromJsonAsync<PagedResponse<TransactionSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(filtered);
        Assert.DoesNotContain(filtered.Items, t => t.TransactionId == initiated.TransactionId);
    }

    [Fact]
    public async Task GetTransactions_ShouldReturn403_ForNonAdministratorCaller()
    {
        // Arrange
        string nonAdminToken = await SignInAsNonAdministratorAsync();

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedGet("/api/transactions", nonAdminToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(Guid BookingId, Guid TransactionId)> CreateSucceededTransactionAsync(string adminToken)
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        Guid bookingId = await HoldAndConfirmBookingAsync(unit.Id);

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId }, TestContext.Current.CancellationToken);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        HttpResponseMessage succeedResponse = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{initiated.TransactionId}/succeed", null, adminToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, succeedResponse.StatusCode);

        return (bookingId, initiated.TransactionId);
    }

    private async Task<Guid> CreateRefundPendingTransactionAsync(string adminToken)
    {
        (Guid bookingId, Guid transactionId) = await CreateSucceededTransactionAsync(adminToken);

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
        booking.Cancel();
        await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Drives the same reversal CancelBookingEndpoint would - seeded
        // directly here rather than through the endpoint since ownership
        // (CustomerId) isn't this test file's concern; CancelBookingTests
        // already covers that path end-to-end.
        AppTransactionsDbContext transactionsDb = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        Transaction transaction = await transactionsDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        transaction.MarkRefundPending();
        await transactionsDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        return transactionId;
    }

    [Fact]
    public async Task Refund_ShouldSetStatusToRefunded_WhenRefundPending()
    {
        // Arrange
        string adminToken = await SignInAsAdministratorAsync();
        Guid transactionId = await CreateRefundPendingTransactionAsync(adminToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{transactionId}/refund", null, adminToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppTransactionsDbContext transactionsDb = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        Transaction transaction = await transactionsDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        Assert.Equal(TransactionStatus.Refunded, transaction.TransactionStatus);
    }

    [Fact]
    public async Task Refund_ShouldReturn409_WhenTransactionIsNotAwaitingRefund()
    {
        // Arrange
        string adminToken = await SignInAsAdministratorAsync();
        (_, Guid transactionId) = await CreateSucceededTransactionAsync(adminToken);

        // Act - Succeeded, not RefundPending
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{transactionId}/refund", null, adminToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Refund_ShouldReturn403_ForNonAdministratorCaller()
    {
        // Arrange
        string adminToken = await SignInAsAdministratorAsync();
        Guid transactionId = await CreateRefundPendingTransactionAsync(adminToken);
        string nonAdminToken = await SignInAsNonAdministratorAsync();

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{transactionId}/refund", null, nonAdminToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RefundFail_ShouldSetStatusToRefundFailedAndRecordReason_WhenRefundPending()
    {
        // Arrange
        string adminToken = await SignInAsAdministratorAsync();
        Guid transactionId = await CreateRefundPendingTransactionAsync(adminToken);

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{transactionId}/refund-fail",
                new { Reason = "Original card closed" }, adminToken),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppTransactionsDbContext transactionsDb = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        Transaction transaction = await transactionsDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        Assert.Equal(TransactionStatus.RefundFailed, transaction.TransactionStatus);
        Assert.Equal("Original card closed", transaction.FailureReason);
    }
}
