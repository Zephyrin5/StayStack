using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Transactions;

// Initiation used to verify the booking was payable and insert the transaction
// as independent operations, so a cancellation committing in between produced a
// pending payment against a cancelled booking.
//
// On its own that is untidy - no money moves today. What promotes it is what it
// manufactures: if the cancellation also moved an earlier payment to
// RefundPending, the active-transaction check matches nothing and the insert
// succeeds, leaving one booking with a RefundPending *and* a Succeeded
// transaction. The active index permits exactly that pair, and any booking-wide
// query written as SingleOrDefault then throws on every retry and every sweep
// pass for as long as both rows exist.
[Collection("Integration Tests")]
public class PaymentInitiationRaceTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly List<Property> _pendingProperties = [];

    private Unit CreateTestUnit()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);
    }

    // Pauses initiation between its booking check and its insert - the window
    // the whole defect lives in.
    private sealed class PauseAfterTheBookingCheck(
        IBookingLookup inner, TaskCompletionSource gate, TaskCompletionSource reached) : IBookingLookup
    {
        private int _fired;

        public async Task<BookingAccessResult?> VerifyBookingAccessAsync(
            Guid bookingId, Guid? customerId, CancellationToken cancellationToken)
        {
            BookingAccessResult? result = await inner.VerifyBookingAccessAsync(bookingId, customerId, cancellationToken);

            if (Interlocked.Increment(ref _fired) == 1)
            {
                reached.TrySetResult();
                await gate.Task;
            }

            return result;
        }

        public Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetBookingAsync(bookingId, cancellationToken);

        public Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
            Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken) =>
            inner.GetConfirmedBookingsForCustomerAsync(customerId, checkOutFrom, checkOutTo, cancellationToken);

        public Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetBookingDetailsAsync(bookingId, cancellationToken);

        public Task<RefundObligationSnapshot?> GetRefundObligationAsync(
            Guid bookingId, CancellationToken cancellationToken) =>
            inner.GetRefundObligationAsync(bookingId, cancellationToken);

        public Task MarkRefundObligationResolvedAsync(
            Guid bookingId, DateTimeOffset resolvedAt, CancellationToken cancellationToken) =>
            inner.MarkRefundObligationResolvedAsync(bookingId, resolvedAt, cancellationToken);
    }

    [Fact]
    public async Task ACancellationCommittingMidInitiation_LeavesNoTransactionBehind()
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(_pendingProperties);
            _pendingProperties.Clear();
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient plain = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(180);

        HoldAvailabilityResponse? hold = await (await plain.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        ConfirmBookingResponse? booking = await (await plain.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking?.ManagementToken);

        string session = await OpenSessionAsync(plain, booking.BookingId, booking.ManagementToken);

        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IBookingLookup));
                services.Remove(original);
                services.AddScoped<IBookingLookup>(sp => new PauseAfterTheBookingCheck(
                    (IBookingLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), gate, reached));
            }));

        using HttpClient paused = host.CreateClient();

        // Act - initiation has seen a payable booking and has not inserted.
        HttpRequestMessage initiate = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = booking.BookingId })
        };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        Task<HttpResponseMessage> initiation = paused.SendAsync(initiate, TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The cancellation, start to finish, inside that window.
        HttpRequestMessage cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{booking.BookingId}/cancel")
        {
            Content = JsonContent.Create(new { bookingId = booking.BookingId, guestEmail = "jane@example.com" })
        };
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);

        Assert.Equal(HttpStatusCode.OK, (await plain.SendAsync(cancel, TestContext.Current.CancellationToken)).StatusCode);

        gate.SetResult();

        HttpResponseMessage response = await initiation;

        // Assert - refused, and nothing left behind. A pending payment against
        // a cancelled booking is the row that goes on to break refund
        // resolution for this booking permanently.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Assert.Empty(await assertScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .Where(t => t.BookingId == booking.BookingId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> OpenSessionAsync(HttpClient client, Guid bookingId, string managementToken)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/bookings/{bookingId}/manage/session",
            new CreateBookingSessionRequest { BookingId = bookingId, ManagementToken = managementToken },
            TestContext.Current.CancellationToken);

        CreateBookingSessionResponse? session = await response.Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        return session.SessionToken;
    }
}
