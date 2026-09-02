using Bogus;
using Bookings;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using BuildingBlocks.Pagination;
using Availability.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
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
using Transactions.Contracts;
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
    // Properties for units built by CreateTestUnit below, flushed by the
    // seeder so a unit is never persisted without its owner - see
    // CatalogSeeding.
    private readonly List<Property> _pendingProperties = [];

    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private Unit CreateTestUnit(decimal basePrice = 100m)
    {
        // Built on a real Property, not a throwaway id - see CatalogSeeding.
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            basePrice);
    }

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // Owners first - a Unit without its Property no longer resolves.
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    // Returns the management token alongside the id: initiating a payment now
    // requires proof of ownership, and for guest checkout that token - issued
    // by confirm, exactly once - is it.
    private async Task<(Guid BookingId, string ManagementToken)> HoldAndConfirmBookingAsync(Guid unitId)
    {
        DateOnly today = CatalogSeeding.Today();
        HttpResponseMessage holdResponse = await _client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
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
        Assert.NotNull(confirmed.ManagementToken);
        return (confirmed.BookingId, confirmed.ManagementToken);
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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        // Act - initiate
        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);

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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);

        HttpResponseMessage firstResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act
        HttpResponseMessage secondResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);

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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);

        // Act: fire concurrent InitiateTransaction requests for the same booking.
        const int concurrentRequests = 10;
        Task<HttpResponseMessage>[] tasks = [.. Enumerable.Range(0, concurrentRequests)
            .Select(_ => factory.CreateClient().PostAsJsonAsync(
                "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken))];

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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);
        string nonAdminToken = await SignInAsNonAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
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
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);

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
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);
        string adminToken = await SignInAsAdministratorAsync();

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
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

    // Carries the management token out too - callers that initiate a second
    // transaction on the same booking need it to prove ownership again.
    private async Task<(Guid BookingId, Guid TransactionId, string ManagementToken)> CreateSucceededTransactionAsync(string adminToken)
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);

        HttpResponseMessage initiateResponse = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
        InitiateTransactionResponse? initiated =
            await initiateResponse.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(initiated);

        HttpResponseMessage succeedResponse = await _client.SendAsync(
            AuthorizedPost($"/api/transactions/{initiated.TransactionId}/succeed", null, adminToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, succeedResponse.StatusCode);

        return (bookingId, initiated.TransactionId, managementToken);
    }

    private async Task<Guid> CreateRefundPendingTransactionAsync(string adminToken)
    {
        (Guid bookingId, Guid transactionId, string managementToken) = await CreateSucceededTransactionAsync(adminToken);

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
        transaction.MarkRefundPending(transaction.Amount);
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
        (_, Guid transactionId, _) = await CreateSucceededTransactionAsync(adminToken);

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

    [Fact]
    public async Task Initiate_WithoutOwnershipProof_Returns404_AndCannotBlockTheRealGuestsPayment()
    {
        // This endpoint used to accept a bare booking id from anyone, alone
        // among the anonymous booking-scoped endpoints. Two things followed,
        // and this covers both.
        //
        // First, the 404-vs-409 split was a status oracle: an unauthenticated
        // caller could tell "no such booking" from "that booking exists but
        // isn't payable". Impractical to enumerate against Guid v7's 74 random
        // bits, but the codebase avoids exactly this elsewhere -
        // HostAuthorization.RequireOwnership returns 404 rather than 403.
        //
        // Second, and worse: a stranger holding the id could open a Pending
        // transaction on it, and ix_transactions_booking_id_active would then
        // reject the real guest's payment with 409. A payment-denial vector.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);

        HttpResponseMessage withoutProof = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId },
            TestContext.Current.CancellationToken);

        // 404, the same answer a booking id that does not exist gets - so the
        // status reveals nothing about whether this one does.
        Assert.Equal(HttpStatusCode.NotFound, withoutProof.StatusCode);

        HttpResponseMessage unknownBooking = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = Guid.CreateVersion7() },
            TestContext.Current.CancellationToken);

        Assert.Equal(unknownBooking.StatusCode, withoutProof.StatusCode);

        // The attempt left nothing behind, so the guest's own payment still
        // goes through rather than hitting "a transaction is already in
        // progress".
        HttpResponseMessage withProof = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, withProof.StatusCode);
    }

    [Fact]
    public async Task Initiate_WithOwnershipProof_StillDistinguishesNotPayableFromNotFound()
    {
        // The oracle is closed by requiring proof, not by flattening the
        // answer. A caller who has proven the booking is theirs is entitled to
        // know why it cannot be paid for - telling them "not found" about a
        // booking they are looking at would be misleading, and they are the
        // only caller who can now reach this branch at all.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        (Guid bookingId, string managementToken) = await HoldAndConfirmBookingAsync(unit.Id);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
            booking.Cancel();
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RefundLookups_StaySingleValued_OnceATransactionEntersTheRefundLifecycle()
    {
        // Pins the invariant that makes TransactionReversal's two
        // SingleOrDefaultAsync lookups safe, because nothing else did and it
        // spans three separate facts across two modules:
        //
        //   1. ix_transactions_booking_id_active is unique but filtered to
        //      ('Pending','Succeeded'), so a transaction entering the refund
        //      sub-lifecycle leaves it and stops blocking new inserts.
        //   2. RefundAmount is only ever written by MarkRefundPending, which
        //      requires Succeeded - so a second non-null RefundAmount needs a
        //      second transaction to reach Succeeded.
        //   3. That second transaction can never be created, because
        //      initiating one requires BookingSummary.IsPending, and a
        //      booking only reaches the refund path by being Cancelled -
        //      with no transition back to Pending.
        //
        // Break (3) - say, by adding a reinstate-booking feature - and
        // GetRefundSnapshotAsync starts throwing InvalidOperationException,
        // surfacing as a 500 on cancellation. This test fails first.
        string adminToken = await SignInAsAdministratorAsync();
        (Guid bookingId, Guid transactionId, string managementToken) = await CreateSucceededTransactionAsync(adminToken);

        using (IServiceScope setupScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext bookingsDb = setupScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            Booking booking = await bookingsDb.Bookings.SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken);
            booking.Cancel();
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            AppTransactionsDbContext transactionsDb = setupScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
            Transaction transaction = await transactionsDb.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
            transaction.MarkRefundPending(transaction.Amount);
            await transactionsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The transaction has left the unique index's filter, so the index
        // alone would now permit a second one. The booking's own state is
        // what actually refuses it.
        HttpResponseMessage secondInitiate = await _client.PostAsJsonAsync(
            "/api/transactions", new InitiateTransactionRequest { BookingId = bookingId, ManagementToken = managementToken }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, secondInitiate.StatusCode);

        // And both lookups still resolve rather than throwing on a second row.
        using IServiceScope scope = factory.Services.CreateScope();
        ITransactionReversal transactionReversal = scope.ServiceProvider.GetRequiredService<ITransactionReversal>();

        TransactionRefundSnapshot? snapshot =
            await transactionReversal.GetRefundSnapshotAsync(bookingId, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);

        // Null, not a throw: the one Succeeded transaction moved on to
        // RefundPending, so nothing matches that filter any more.
        Assert.Null(await transactionReversal.GetSucceededTransactionAmountAsync(bookingId, TestContext.Current.CancellationToken));
    }
}
