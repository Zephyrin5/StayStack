using Bogus;
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using BuildingBlocks.Persistence;
using Catalog;
using Catalog.Entities;
using Identity.Entities;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Transactions.Features.InitiateTransaction;
namespace IntegrationTests.Features.Bookings;

// An obligation used to have one ending: a refund. Everything else stayed unresolved and came back
// every run of the sweep forever, because "no payment yet" and "no payment ever" read the same. These
// pin the second ending - nothing owed - and where each writer reaches it (docs/adr/0027).
[Collection(BookingsCollection.Name)]
public class RefundObligationTerminationTests(BookingsFixture factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();
    private readonly List<Property> _pendingProperties = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private async Task SeedCatalogAsync(params object[] units)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb catalog = scope.ServiceProvider.GetRequiredService<CatalogDb>();
        catalog.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        catalog.AddRange(units);
        await catalog.SaveChangesAsync(Ct);
    }

    private async Task<RefundObligation?> ReadObligationAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BookingsDb>().RefundObligations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.BookingId == bookingId, Ct);
    }

    // ---- the checkout flow, the same round trips a client makes -------------

    private async Task<string> SignedInCustomerAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            IdentityResult created = await users.CreateAsync(
                new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email }, password);
            Assert.True(created.Succeeded, "Failed to seed the customer.");
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/auth/sign-in", new SignInRequest { Email = email, Password = password }, Ct);
        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, Ct);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<string> SignedInAdministratorAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = IntegrationTestAdmin.Email,
            Password = IntegrationTestAdmin.Password
        }, Ct);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, Ct);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<Guid> BookAsync(Guid unitId, string customerToken)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(30);

        HttpResponseMessage held = await _client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = checkIn,
            CheckOut = checkIn.AddDays(2),
            GuestCount = 2
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        HoldAvailabilityResponse? hold = await held.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, Ct);
        Assert.NotNull(hold);

        using HttpRequestMessage confirm = new HttpRequestMessage(HttpMethod.Post, "/api/bookings")
        {
            Content = JsonContent.Create(new ConfirmBookingRequest
            {
                HoldId = hold.HoldId,
                GuestName = _faker.Name.FullName(),
                GuestEmail = _faker.Internet.Email()
            })
        };
        confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

        HttpResponseMessage confirmed = await _client.SendAsync(confirm, Ct);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        ConfirmBookingResponse? booking = await confirmed.Content.ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, Ct);
        Assert.NotNull(booking);
        return booking.BookingId;
    }

    private async Task<Guid> InitiatePaymentAsync(Guid bookingId, string customerToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent.Create(new InitiateTransactionRequest { BookingId = bookingId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

        HttpResponseMessage response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        InitiateTransactionResponse? result = await response.Content.ReadFromJsonAsync<InitiateTransactionResponse>(TestJsonOptions.Default, Ct);
        Assert.NotNull(result);
        return result.TransactionId;
    }

    private async Task SettlePaymentAsync(Guid transactionId, string outcome, string adminToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/transactions/{transactionId}/{outcome}")
        {
            Content = JsonContent.Create(new { reason = "declined by the provider" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request, Ct)).StatusCode);
    }

    private async Task CancelAsync(Guid bookingId, string customerToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel")
        {
            Content = JsonContent.Create(new { bookingId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request, Ct)).StatusCode);
    }

    // ---- the four endings ---------------------------------------------------

    [Fact]
    public async Task CancellingAnUnpaidBooking_SettlesItsObligationInTheSameRequest()
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SignedInCustomerAsync();
        Guid bookingId = await BookAsync(unit.Id, customerToken);

        await CancelAsync(bookingId, customerToken);

        // Nobody paid and nobody can now, so the obligation is finished where it was written rather
        // than left for a sweep with nothing to do.
        RefundObligation? obligation = await ReadObligationAsync(bookingId);
        Assert.NotNull(obligation);
        Assert.NotNull(obligation.ResolvedAt);
        Assert.Equal(RefundObligationOutcome.NothingOwed, obligation.Outcome);
    }

    [Fact]
    public async Task CancellingWithAPaymentStillPending_LeavesTheObligationOpen_UntilThatPaymentFails()
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SignedInCustomerAsync();
        string adminToken = await SignedInAdministratorAsync();
        Guid bookingId = await BookAsync(unit.Id, customerToken);
        Guid transactionId = await InitiatePaymentAsync(bookingId, customerToken);

        await CancelAsync(bookingId, customerToken);

        // The payment could still succeed, and if it does the guest is owed the money. This is the
        // case the obligation exists for, so it stays open.
        RefundObligation? pending = await ReadObligationAsync(bookingId);
        Assert.NotNull(pending);
        Assert.Null(pending.ResolvedAt);
        Assert.Null(pending.Outcome);

        await SettlePaymentAsync(transactionId, "fail", adminToken);

        // The last payment that could have paid it is gone, so the obligation ends with the failure
        // rather than waiting on the sweep.
        RefundObligation? settled = await ReadObligationAsync(bookingId);
        Assert.NotNull(settled);
        Assert.NotNull(settled.ResolvedAt);
        Assert.Equal(RefundObligationOutcome.NothingOwed, settled.Outcome);
    }

    [Fact]
    public async Task CancellingWithAPaymentStillPending_ThatThenSucceeds_RecordsTheFullRefund()
    {
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);
        string customerToken = await SignedInCustomerAsync();
        string adminToken = await SignedInAdministratorAsync();
        Guid bookingId = await BookAsync(unit.Id, customerToken);
        Guid transactionId = await InitiatePaymentAsync(bookingId, customerToken);

        await CancelAsync(bookingId, customerToken);
        await SettlePaymentAsync(transactionId, "succeed", adminToken);

        // Paid after the cancellation, so the payment bought nothing and the whole amount goes back -
        // the policy percentage never applies to money taken for a stay that was already cancelled.
        using IServiceScope scope = factory.Services.CreateScope();
        global::Transactions.Entities.Transaction payment = await scope.ServiceProvider
            .GetRequiredService<global::Transactions.TransactionsDb>().Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, Ct);

        Assert.NotNull(payment.RefundAmount);
        Assert.Equal(payment.Amount.Amount, payment.RefundAmount.Value.Amount);

        RefundObligation? obligation = await ReadObligationAsync(bookingId);
        Assert.NotNull(obligation);
        Assert.NotNull(obligation.ResolvedAt);
        Assert.Equal(RefundObligationOutcome.RefundRecorded, obligation.Outcome);
    }

    [Fact]
    public async Task FiftyExpiredUnpaidBookings_LeaveFiftyObligations_AllNothingOwed()
    {
        // The volume case, and the one that used to accumulate: an unpaid expiry was the ordinary
        // workload, and every one of them stayed on the sweep's books permanently.
        const int count = 50;
        Unit[] units = Enumerable.Range(0, count).Select(_ => CreateTestUnit()).ToArray();
        await SeedCatalogAsync(units);

        DateOnly checkIn = CatalogSeeding.Today().AddDays(40);
        DateTimeOffset overdue = DateTimeOffset.UtcNow.AddMinutes(-1);
        List<Guid> bookingIds = [];

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();

            foreach (Unit unit in units)
            {
                UnitAvailabilityHold hold = new UnitAvailabilityHold
                {
                    Id = Guid.CreateVersion7(),
                    UnitId = unit.Id,
                    StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
                    Status = "pending_payment",
                    GuestCount = 2,
                    CreatedAt = DateTimeOffset.UtcNow,
                    TotalPrice = Money.Of(200m, Currency.KWD),
                    Subtotal = 200m
                };

                Booking booking = Booking.Create(
                    Guid.CreateVersion7(), unit.Id, hold.Id, null,
                    "Jane Guest", "jane@example.com", null,
                    checkIn, checkIn.AddDays(2), 2,
                    Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
                    CancellationPolicy.CreateDefault(), "Asia/Kuwait", overdue);

                bookings.UnitAvailabilityHolds.Add(hold);
                bookings.Bookings.Add(booking);
                bookingIds.Add(booking.Id);
            }

            await bookings.SaveChangesAsync(Ct);
        }

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            await new ExpireUnpaidBookingsJob(
                    jobScope.ServiceProvider.GetRequiredService<BookingsDb>(),
                    jobScope.ServiceProvider.GetRequiredService<ITransactionRunner>(),
                    jobScope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
                    jobScope.ServiceProvider.GetRequiredService<global::Promotions.Contracts.IPromotionRedemption>(),
                    jobScope.ServiceProvider.GetRequiredService<IPaymentReversal>(),
                    TimeProvider.System,
                    NullLogger<ExpireUnpaidBookingsJob>.Instance)
                .ExpireAsync(null!, Ct);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        List<RefundObligation> obligations = await assertScope.ServiceProvider.GetRequiredService<BookingsDb>()
            .RefundObligations.AsNoTracking()
            .Where(o => bookingIds.Contains(o.BookingId))
            .ToListAsync(Ct);

        Assert.Equal(count, obligations.Count);
        Assert.All(obligations, o =>
        {
            Assert.NotNull(o.ResolvedAt);
            Assert.Equal(RefundObligationOutcome.NothingOwed, o.Outcome);
        });
    }

    [Fact]
    public async Task TheSweepsCandidates_ExcludeResolvedObligations_AndThoseWhosePaymentsAreAllFailed()
    {
        // The query the sweep composes, asserted directly. Two rows must stay out of it for different
        // reasons - one is finished, the other has nothing left that could pay it - and one must stay
        // in, or the sweep would stop being a backstop at all.
        Unit unit = CreateTestUnit();
        Unit second = CreateTestUnit();
        Unit third = CreateTestUnit();
        await SeedCatalogAsync(unit, second, third);
        string customerToken = await SignedInCustomerAsync();
        string adminToken = await SignedInAdministratorAsync();

        Guid settled = await BookAsync(unit.Id, customerToken);
        await CancelAsync(settled, customerToken);

        Guid waiting = await BookAsync(second.Id, customerToken);
        await InitiatePaymentAsync(waiting, customerToken);
        await CancelAsync(waiting, customerToken);

        Guid abandoned = await BookAsync(third.Id, customerToken);
        Guid abandonedPayment = await InitiatePaymentAsync(abandoned, customerToken);
        await CancelAsync(abandoned, customerToken);
        await SettlePaymentAsync(abandonedPayment, "fail", adminToken);

        // Reopened deliberately: this is the row Phase 2 inherited, an obligation left unresolved
        // behind a payment that can no longer pay it. The predicate, not the resolution, is what has
        // to keep it out of the sweep.
        using (IServiceScope reopen = factory.Services.CreateScope())
        {
            await reopen.ServiceProvider.GetRequiredService<BookingsDb>().RefundObligations
                .Where(o => o.BookingId == abandoned)
                .ExecuteUpdateAsync(
                    o => o.SetProperty(row => row.ResolvedAt, (DateTimeOffset?)null)
                        .SetProperty(row => row.Outcome, (RefundObligationOutcome?)null),
                    Ct);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        BookingsDb bookings = scope.ServiceProvider.GetRequiredService<BookingsDb>();
        IQueryable<Guid> unsettled = scope.ServiceProvider.GetRequiredService<IPaymentReversal>()
            .BookingIdsWithAnUnsettledPayment();

        List<Guid> candidates = await bookings.RefundObligations.AsNoTracking()
            .Where(o => o.ResolvedAt == null && unsettled.Contains(o.BookingId))
            .Select(o => o.BookingId)
            .ToListAsync(Ct);

        Assert.Contains(waiting, candidates);
        Assert.DoesNotContain(settled, candidates);
        Assert.DoesNotContain(abandoned, candidates);
    }
}
