using Bookings;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.CreateBookingSession;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using Bookings.Outbox;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions;
using Transactions.Contracts;
using Transactions.Entities;
using Bookings.Contracts;
namespace IntegrationTests.Features.Bookings;

// A cancellation response reporting a refund that has not been recorded yet
// must report the refund that will be. The resolver decides that amount from
// the obligation; the response used to recompute guest policy instead, and the
// two disagreed exactly where the amount is not guest policy - so a guest was
// shown 50% pending against a durable 100%.
//
// Check-in is three days out in both tests, inside the default policy's 50%
// tier, so a response computing guest policy is visibly wrong rather than
// coincidentally right.
[Collection("Integration Tests")]
public class PendingRefundReportingTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed record Checkout(Guid BookingId, string Session);

    private async Task<Checkout> CheckOutAsync(int daysUntilCheckIn)
    {
        Property property = CatalogSeeding.CreateProperty();
        Unit unit = Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100m);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(property, unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpClient client = factory.CreateClient();
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysUntilCheckIn);

        HoldAvailabilityResponse? hold = await (await client.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        ConfirmBookingResponse? booking = await (await client.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking?.ManagementToken);

        CreateBookingSessionResponse? session = await (await client.PostAsJsonAsync(
                $"/api/bookings/{booking.BookingId}/manage/session",
                new CreateBookingSessionRequest
                {
                    BookingId = booking.BookingId,
                    ManagementToken = booking.ManagementToken
                }, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<CreateBookingSessionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        return new Checkout(booking.BookingId, session.SessionToken);
    }

    private async Task<CancelBookingResponse> CancelAsync(Checkout checkout)
    {
        HttpRequestMessage cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{checkout.BookingId}/cancel")
        {
            Content = JsonContent.Create(new { bookingId = checkout.BookingId, guestEmail = "jane@example.com" })
        };
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", checkout.Session);

        HttpResponseMessage response = await factory.CreateClient().SendAsync(cancel, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CancelBookingResponse? body = await response.Content
            .ReadFromJsonAsync<CancelBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    // A payment committed Succeeded with nothing dispatched - the delayed
    // resolution, modelled by not delivering it.
    private async Task PaySilentlyAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Money total = (await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == bookingId, TestContext.Current.CancellationToken)).TotalPrice;

        AppTransactionsDbContext transactions = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
        Transaction payment = Transaction.Create(Guid.CreateVersion7(), bookingId, total);
        payment.MarkSucceeded(DateTimeOffset.UtcNow);
        transactions.Transactions.Add(payment);
        await transactions.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Lets the resolver do what it will eventually do anyway, and reads back
    // what it recorded.
    private async Task<Money> ResolveAsync(Guid bookingId)
    {
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITransactionReversal>()
                .ResolveRefundAsync(bookingId, TestContext.Current.CancellationToken);
        }

        using IServiceScope readScope = factory.Services.CreateScope();

        Transaction refunded = await readScope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>()
            .Transactions.AsNoTracking()
            .SingleAsync(t => t.BookingId == bookingId, TestContext.Current.CancellationToken);

        Assert.NotNull(refunded.RefundAmount);
        return refunded.RefundAmount.Value;
    }

    private static void AssertReportsTheRecordedRefund(CancelBookingResponse pending, Money recorded)
    {
        Assert.True(pending.RefundPending);
        Assert.Equal(recorded.Amount, pending.RefundAmount);
        Assert.Equal(recorded.Currency, pending.Currency);
    }

    [Fact]
    public async Task AnExpiredBookingsPendingRefund_IsReportedAtTheAmountItSettlesAt()
    {
        Checkout checkout = await CheckOutAsync(daysUntilCheckIn: 3);

        // Overdue on this row alone - advancing a clock would expire every
        // other test's pending booking in the shared database too.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().Bookings
                .Where(b => b.Id == checkout.BookingId)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(b => b.PaymentDueAt, DateTimeOffset.UtcNow.AddMinutes(-1)),
                    TestContext.Current.CancellationToken);
        }

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            await new ExpireUnpaidBookingsJob(
                    jobScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
                    jobScope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                    jobScope.ServiceProvider.GetRequiredService<ITransactionLookup>(),
                    jobScope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
                    TimeProvider.System,
                    NullLogger<ExpireUnpaidBookingsJob>.Instance)
                .ExpireAsync(null!, TestContext.Current.CancellationToken);
        }

        // The payment lands after the expiry, and its resolution is delayed.
        await PaySilentlyAsync(checkout.BookingId);

        // Act - the guest asks.
        CancelBookingResponse pending = await CancelAsync(checkout);
        Assert.Equal(BookingStatus.Cancelled, pending.BookingStatus);

        // Assert - the same figure the resolver then records: the full price,
        // since the guest did not ask for this cancellation. Before, 50%.
        Money recorded = await ResolveAsync(checkout.BookingId);

        AssertReportsTheRecordedRefund(pending, recorded);
        Assert.Equal(100m, pending.RefundPercent);
    }

    [Fact]
    public async Task APaymentLandingAfterAGuestCancellation_IsReportedAsTheFullRefundItSettlesAt()
    {
        Checkout checkout = await CheckOutAsync(daysUntilCheckIn: 3);

        // Cancelled with nothing paid - no refund, and an obligation at the
        // guest-policy amount of 50%.
        CancelBookingResponse cancelled = await CancelAsync(checkout);
        Assert.Null(cancelled.RefundAmount);

        // Then a payment succeeds for a booking that no longer exists.
        await PaySilentlyAsync(checkout.BookingId);

        // Act
        CancelBookingResponse pending = await CancelAsync(checkout);

        // Assert - it bought nothing, so all of it goes back, and the response
        // says so rather than applying a policy the payment never engaged.
        Money recorded = await ResolveAsync(checkout.BookingId);

        AssertReportsTheRecordedRefund(pending, recorded);
        Assert.Equal(100m, pending.RefundPercent);
    }
}
