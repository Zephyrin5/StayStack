using Bogus;
using Bookings;
using Bookings.Features.CancelBooking;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.HoldAvailability;
using Bookings.Jobs;
using Bookings.Outbox;
using Bookings.Serialization;
using Catalog;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Hosts;
using Identity;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Promotions;
using Promotions.Enums;
using Promotions.Features.CreatePromotion;
using Reviews;
using SeedWork.Enums;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Transactions;
using Transactions.Entities;
using Transactions.Features.InitiateTransaction;
using Transactions.Outbox;
using Transactions.Serialization;
namespace IntegrationTests.Measurements;

// Stage 0 of the transaction ownership refactor: measurements, not assertions
// about correctness. Skipped unless STAYSTACK_MEASURE names an output directory,
// so they never run in the ordinary suite. Each writes its numbers to a file
// there.
[Collection("Integration Tests")]
public class TransactionTopologyMeasurements(IntegrationTestWebApplicationFactory factory)
{
    private static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("STAYSTACK_MEASURE");
    private readonly Faker _faker = new Faker();

    private static void RequireMeasurementRun() =>
        Assert.SkipUnless(OutputDirectory is { Length: > 0 }, "Measurement only: set STAYSTACK_MEASURE to an output directory.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- host construction -------------------------------------------------

    private WebApplicationFactory<Program> MeteredHost(ConnectionMeter meter, string? connectionString = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (connectionString is not null)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    [new KeyValuePair<string, string?>("ConnectionStrings:AppConnection", connectionString)]));
            }

            builder.ConfigureServices(services =>
            {
                Configure<AppIdentityDbContext>(services, meter, connectionString);
                Configure<AppCatalogDbContext>(services, meter, connectionString);
                Configure<AppHostsDbContext>(services, meter, connectionString);
                Configure<AppPromotionsDbContext>(services, meter, connectionString);
                Configure<AppBookingsDbContext>(services, meter, connectionString);
                Configure<AppTransactionsDbContext>(services, meter, connectionString);
                Configure<AppReviewsDbContext>(services, meter, connectionString);
            });
        });

    private static void Configure<TContext>(IServiceCollection services, ConnectionMeter meter, string? connectionString)
        where TContext : DbContext =>
        services.ConfigureDbContext<TContext>(options =>
        {
            options.AddInterceptors(meter);
            if (connectionString is not null)
            {
                options.UseNpgsql(connectionString);
            }
        });

    private static HttpClient ClientPreservingContext(WebApplicationFactory<Program> host)
    {
        // Starts the host; the flag must be set before the client is created.
        host.Server.PreserveExecutionContext = true;
        return host.CreateClient();
    }

    private static async Task<ConnectionMeter.Operation> MeasureAsync(string name, Func<Task> action)
    {
        ConnectionMeter.Operation operation = ConnectionMeter.Begin(name);
        try
        {
            await action();
        }
        finally
        {
            ConnectionMeter.End();
        }

        return operation;
    }

    // ---- flows ---------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<T> ReadOkAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<T>(body, TestJsonOptions.Default)!;
    }

    private async Task<string> SeedUserAsync(WebApplicationFactory<Program> host, HttpClient client)
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";
        using (IServiceScope scope = host.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            IdentityResult created = await users.CreateAsync(new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email }, password);
            Assert.True(created.Succeeded);
        }

        SignInResponse signIn = await ReadOkAsync<SignInResponse>(
            await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Email = email, Password = password }, Ct));
        return signIn.AccessToken;
    }

    private async Task<(string HostToken, Guid UnitId)> SeedHostWithUnitAsync(WebApplicationFactory<Program> host, HttpClient client)
    {
        string userToken = await SeedUserAsync(host, client);
        BecomeHostResponse becameHost = await ReadOkAsync<BecomeHostResponse>(await client.SendAsync(
            Request(HttpMethod.Post, "/api/hosts/become", userToken,
                new BecomeHostRequest { BusinessName = _faker.Company.CompanyName(), ContactEmail = _faker.Internet.Email() }), Ct));
        string hostToken = becameHost.AccessToken;

        CreatePropertyResponse property = await ReadOkAsync<CreatePropertyResponse>(await client.SendAsync(
            Request(HttpMethod.Post, "/api/catalog/properties", hostToken, new CreatePropertyRequest
            {
                TimeZoneId = "Asia/Kuwait",
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { { "en", "Measured Property" } },
                City = "Kuwait City"
            }), Ct));

        CreateUnitResponse unit = await ReadOkAsync<CreateUnitResponse>(await client.SendAsync(
            Request(HttpMethod.Post, "/api/catalog/units", hostToken, new CreateUnitRequest
            {
                PropertyId = property.PropertyId,
                Name = new Dictionary<string, string> { { "en", "Measured Unit" } },
                MaxOccupancy = 4,
                BasePrice = 100m,
                Currency = Currency.KWD
            }), Ct));

        return (hostToken, unit.UnitId);
    }

    private async Task<string> CreatePromotionAsync(HttpClient client, string hostToken, int? maxRedemptions = null)
    {
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Post, "/api/promotions", hostToken, new CreatePromotionRequest
        {
            Code = code,
            DiscountType = PromotionDiscountType.Percentage,
            DiscountValue = 10m,
            MaxRedemptions = maxRedemptions
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return code;
    }

    private static async Task<Guid> HoldAsync(HttpClient client, Guid unitId, int daysOut, int nights = 2)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(daysOut);
        HoldAvailabilityResponse hold = await ReadOkAsync<HoldAvailabilityResponse>(await client.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest { UnitId = unitId, CheckIn = checkIn, CheckOut = checkIn.AddDays(nights), GuestCount = 2 }, Ct));
        return hold.HoldId;
    }

    private HttpRequestMessage ConfirmRequest(Guid holdId, string? customerToken, string? promoCode) =>
        Request(HttpMethod.Post, "/api/bookings", customerToken, new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = _faker.Name.FullName(),
            GuestEmail = _faker.Internet.Email(),
            PromoCode = promoCode
        });

    private static HttpRequestMessage InitiateRequest(Guid bookingId, string customerToken) =>
        Request(HttpMethod.Post, "/api/transactions", customerToken, new InitiateTransactionRequest { BookingId = bookingId });

    private static HttpRequestMessage SucceedRequest(Guid transactionId, string adminToken) =>
        Request(HttpMethod.Post, $"/api/transactions/{transactionId}/succeed", adminToken);

    private static HttpRequestMessage CancelRequest(Guid bookingId, string customerToken) =>
        Request(HttpMethod.Post, $"/api/bookings/{bookingId}/cancel", customerToken, new CancelBookingRequest { BookingId = bookingId });

    // ---- 0.2 -----------------------------------------------------------------

    [Fact]
    public async Task PeakConnectionsPerOperation()
    {
        RequireMeasurementRun();

        ConnectionMeter meter = new ConnectionMeter();
        await using WebApplicationFactory<Program> host = MeteredHost(meter);
        HttpClient client = ClientPreservingContext(host);

        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync(host, client);
        string customerToken = await SeedUserAsync(host, client);
        string adminToken = await IntegrationTestAdmin.SignInAsync(client, Ct);
        string code = await CreatePromotionAsync(client, hostToken);
        List<ConnectionMeter.Operation> results = [];

        // Confirm, no promo code.
        Guid hold1 = await HoldAsync(client, unitId, 60);
        ConfirmBookingResponse? plain = null;
        results.Add(await MeasureAsync("POST /api/bookings (no promo)", async () =>
            plain = await ReadOkAsync<ConfirmBookingResponse>(await client.SendAsync(ConfirmRequest(hold1, customerToken, null), Ct))));

        // Confirm with a promo code.
        Guid hold2 = await HoldAsync(client, unitId, 70);
        ConfirmBookingResponse? promo = null;
        results.Add(await MeasureAsync("POST /api/bookings (promo)", async () =>
            promo = await ReadOkAsync<ConfirmBookingResponse>(await client.SendAsync(ConfirmRequest(hold2, customerToken, code), Ct))));

        // Payment success, including its inline ConfirmBookingPayment dispatch.
        InitiateTransactionResponse initiated = await ReadOkAsync<InitiateTransactionResponse>(
            await client.SendAsync(InitiateRequest(promo!.BookingId, customerToken), Ct));
        results.Add(await MeasureAsync("POST /api/transactions/{id}/succeed", async () =>
            await ReadOkAsync<object>(await client.SendAsync(SucceedRequest(initiated.TransactionId, adminToken), Ct))));

        // Cancel a paid booking that redeemed a code: obligation, refund and
        // redemption reversal, with their inline dispatches.
        results.Add(await MeasureAsync("POST /api/bookings/{id}/cancel (paid, promo)", async () =>
            await ReadOkAsync<object>(await client.SendAsync(CancelRequest(promo.BookingId, customerToken), Ct))));

        // Cancel an unpaid booking without a code.
        results.Add(await MeasureAsync("POST /api/bookings/{id}/cancel (unpaid)", async () =>
            await ReadOkAsync<object>(await client.SendAsync(CancelRequest(plain!.BookingId, customerToken), Ct))));

        // One Bookings relay tick over pending rows: a redemption reversal and a
        // refund resolution for a real cancelled-and-paid booking.
        Guid hold3 = await HoldAsync(client, unitId, 80);
        ConfirmBookingResponse third = await ReadOkAsync<ConfirmBookingResponse>(
            await client.SendAsync(ConfirmRequest(hold3, customerToken, await CreatePromotionAsync(client, hostToken)), Ct));
        using (IServiceScope scope = host.Services.CreateScope())
        {
            BookingsOutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>();
            dispatcher.Enqueue(new ReverseRedemptionOutboxMessage(third.BookingId), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);
            dispatcher.Enqueue(new ReverseTransactionOutboxMessage(third.BookingId), BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
            await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().SaveChangesAsync(Ct);
        }

        results.Add(await MeasureAsync("Bookings OutboxRelayJob tick (2 pending rows)", async () =>
        {
            using IServiceScope scope = host.Services.CreateScope();
            await ActivatorUtilities.CreateInstance<OutboxRelayJob>(scope.ServiceProvider).RelayAsync(null!, Ct);
        }));

        // One Transactions relay tick over a pending payment confirmation.
        Guid hold4 = await HoldAsync(client, unitId, 90);
        ConfirmBookingResponse fourth = await ReadOkAsync<ConfirmBookingResponse>(
            await client.SendAsync(ConfirmRequest(hold4, customerToken, null), Ct));
        using (IServiceScope scope = host.Services.CreateScope())
        {
            AppTransactionsDbContext transactions = scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>();
            Transaction transaction = Transaction.Create(Guid.CreateVersion7(), fourth.BookingId, SeedWork.ValueObjects.Money.Of(fourth.TotalPrice, Currency.KWD));
            transaction.MarkSucceeded(DateTimeOffset.UtcNow);
            transactions.Transactions.Add(transaction);
            TransactionsOutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<TransactionsOutboxDispatcher>();
            dispatcher.Enqueue(new ConfirmBookingPaymentOutboxMessage(transaction.Id, fourth.BookingId),
                TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage);
            await transactions.SaveChangesAsync(Ct);
        }

        results.Add(await MeasureAsync("Transactions OutboxRelayJob tick (1 pending row)", async () =>
        {
            using IServiceScope scope = host.Services.CreateScope();
            await ActivatorUtilities.CreateInstance<Transactions.Jobs.OutboxRelayJob>(scope.ServiceProvider).RelayAsync(null!, Ct);
        }));

        StringBuilder report = new StringBuilder();
        foreach (ConnectionMeter.Operation operation in results)
        {
            report.AppendLine(
                $"{operation.Name,-50} peak={operation.Peak}  opens={operation.Opens}  at-peak=[{string.Join(", ", operation.PeakContexts.OrderBy(c => c))}]");
        }

        await File.WriteAllTextAsync(Path.Combine(OutputDirectory!, "0.2-connection-peaks.txt"), report.ToString(), Ct);
    }

    // ---- 0.4 -----------------------------------------------------------------

    private sealed record Outcome(string Label, int Status, double Ms, string Detail);

    private static string SmallPool(string connectionString, int size) =>
        new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = size }.ConnectionString;

    [Fact]
    public async Task ConcurrentConfirmsDuringRelay_AtMaxPoolSize5()
    {
        RequireMeasurementRun();

        // Seeded through the ordinary host and its own pool, so seeding does not
        // compete for the five connections under test.
        HttpClient seedClient = factory.CreateClient();
        (string hostToken, Guid unitId) = await SeedHostWithUnitAsync(factory, seedClient);
        string code = await CreatePromotionAsync(seedClient, hostToken);

        const int confirms = 20;
        List<Guid> holds = [];
        for (int i = 0; i < confirms; i++)
        {
            holds.Add(await HoldAsync(seedClient, unitId, 100 + i * 3));
        }

        // Relay load: rows whose handlers open a second module's context while
        // the claim transaction is held. Booking ids that do not exist, so each
        // handler does its lookup and finds nothing to change.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            BookingsOutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>();
            for (int i = 0; i < 1000; i++)
            {
                dispatcher.Enqueue(new ReverseRedemptionOutboxMessage(Guid.CreateVersion7()), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);
                dispatcher.Enqueue(new ReverseTransactionOutboxMessage(Guid.CreateVersion7()), BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
            }

            await scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().SaveChangesAsync(Ct);
        }

        string report = await RunBurstAsync(
            "20 concurrent confirms (half with a promo code) + Bookings and Transactions relay loops, MaxPoolSize=5",
            holds.Select((holdId, i) => (Func<HttpClient, HttpRequestMessage>)(_ => ConfirmRequest(holdId, null, i % 2 == 0 ? code : null))).ToList());

        await File.WriteAllTextAsync(Path.Combine(OutputDirectory!, "0.4-confirms-pool5.txt"), report, Ct);
    }

    [Fact]
    public async Task ConcurrentPaymentSuccessesDuringRelay_AtMaxPoolSize5()
    {
        RequireMeasurementRun();

        HttpClient seedClient = factory.CreateClient();
        (_, Guid unitId) = await SeedHostWithUnitAsync(factory, seedClient);
        string customerToken = await SeedUserAsync(factory, seedClient);
        string adminToken = await IntegrationTestAdmin.SignInAsync(seedClient, Ct);

        const int payments = 20;
        List<Guid> transactionIds = [];
        for (int i = 0; i < payments; i++)
        {
            Guid holdId = await HoldAsync(seedClient, unitId, 200 + i * 3);
            ConfirmBookingResponse booking = await ReadOkAsync<ConfirmBookingResponse>(
                await seedClient.SendAsync(ConfirmRequest(holdId, customerToken, null), Ct));
            InitiateTransactionResponse initiated = await ReadOkAsync<InitiateTransactionResponse>(
                await seedClient.SendAsync(InitiateRequest(booking.BookingId, customerToken), Ct));
            transactionIds.Add(initiated.TransactionId);
        }

        string report = await RunBurstAsync(
            "20 concurrent payment successes (each dispatches ConfirmBookingPayment inline) + relay loops, MaxPoolSize=5",
            transactionIds.Select(id => (Func<HttpClient, HttpRequestMessage>)(_ => SucceedRequest(id, adminToken))).ToList());

        await File.WriteAllTextAsync(Path.Combine(OutputDirectory!, "0.4-payments-pool5.txt"), report, Ct);
    }

    private async Task<string> RunBurstAsync(string title, List<Func<HttpClient, HttpRequestMessage>> requests)
    {
        string smallPool = SmallPool(factory.ConnectionString, 5);
        ConnectionMeter meter = new ConnectionMeter();
        await using WebApplicationFactory<Program> host = MeteredHost(meter, smallPool);
        HttpClient client = host.CreateClient();

        // Warm the host before timing anything.
        await client.GetAsync("/api/localization/languages", Ct);

        using CancellationTokenSource stopRelay = new CancellationTokenSource();
        ConcurrentBag<string> relayErrors = [];
        int relayTicks = 0;

        async Task RelayLoopAsync<TJob>(Func<TJob, Task> tick) where TJob : notnull
        {
            while (!stopRelay.IsCancellationRequested)
            {
                try
                {
                    using IServiceScope scope = host.Services.CreateScope();
                    await tick(ActivatorUtilities.CreateInstance<TJob>(scope.ServiceProvider));
                    Interlocked.Increment(ref relayTicks);
                }
                catch (Exception ex)
                {
                    relayErrors.Add($"{typeof(TJob).FullName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Peak total and idle-in-transaction backends, sampled on a separate
        // connection outside the pool under test.
        int peakBackends = 0, peakIdleInTransaction = 0;
        Task sampler = Task.Run(async () =>
        {
            await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            while (!stopRelay.IsCancellationRequested)
            {
                await using NpgsqlCommand command = new NpgsqlCommand(
                    "SELECT count(*), count(*) FILTER (WHERE state = 'idle in transaction') FROM pg_stat_activity " +
                    "WHERE datname = current_database() AND pid <> pg_backend_pid() AND backend_type = 'client backend'", connection);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);
                await reader.ReadAsync(CancellationToken.None);
                peakBackends = Math.Max(peakBackends, (int)reader.GetInt64(0));
                peakIdleInTransaction = Math.Max(peakIdleInTransaction, (int)reader.GetInt64(1));
                await reader.CloseAsync();
                await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None);

        Task bookingsRelay = Task.Run(() => RelayLoopAsync<OutboxRelayJob>(job => job.RelayAsync(null!, CancellationToken.None)), CancellationToken.None);
        Task transactionsRelay = Task.Run(() => RelayLoopAsync<Transactions.Jobs.OutboxRelayJob>(job => job.RelayAsync(null!, CancellationToken.None)), CancellationToken.None);

        // The burst starts only once both relays are dispatching.
        while (relayTicks < 2 && relayErrors.IsEmpty)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        long pendingBefore = await PendingBookingsRowsAsync();
        ConcurrentBag<Outcome> outcomes = [];
        Stopwatch wall = Stopwatch.StartNew();
        List<Task> burst = requests.Select((build, i) => Task.Run(async () =>
        {
            Stopwatch one = Stopwatch.StartNew();
            try
            {
                using HttpRequestMessage request = build(client);
                using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
                string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                outcomes.Add(new Outcome($"#{i}", (int)response.StatusCode, one.Elapsed.TotalMilliseconds,
                    response.StatusCode == HttpStatusCode.OK ? "" : body.Length > 240 ? body[..240] : body));
            }
            catch (Exception ex)
            {
                outcomes.Add(new Outcome($"#{i}", -1, one.Elapsed.TotalMilliseconds, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }, CancellationToken.None)).ToList();

        TimeSpan hangThreshold = TimeSpan.FromMinutes(5);
        Task all = Task.WhenAll(burst);
        bool completed = await Task.WhenAny(all, Task.Delay(hangThreshold, CancellationToken.None)) == all;
        double wallMs = wall.Elapsed.TotalMilliseconds;
        long pendingAfter = await PendingBookingsRowsAsync();

        await stopRelay.CancelAsync();
        Task relays = Task.WhenAll(bookingsRelay, transactionsRelay, sampler);
        bool relaysStopped = await Task.WhenAny(relays, Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None)) == relays;

        StringBuilder report = new StringBuilder();
        report.AppendLine(title);
        report.AppendLine($"completed={completed} (threshold {hangThreshold.TotalSeconds:F0}s)  wall_ms={wallMs:F0}  relays_stopped={relaysStopped}  relay_ticks={relayTicks}");
        report.AppendLine($"bookings_outbox_pending: before_burst={pendingBefore} after_burst={pendingAfter}");
        report.AppendLine($"peak_client_backends={peakBackends}  peak_idle_in_transaction={peakIdleInTransaction}");
        report.AppendLine($"requests: finished={outcomes.Count}/{requests.Count}  ok={outcomes.Count(o => o.Status == 200)}  " +
                          $"p50_ms={Percentile(outcomes, 0.5):F0}  p95_ms={Percentile(outcomes, 0.95):F0}  max_ms={(outcomes.IsEmpty ? 0 : outcomes.Max(o => o.Ms)):F0}");
        foreach (IGrouping<int, Outcome> byStatus in outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key))
        {
            report.AppendLine($"  status {byStatus.Key}: {byStatus.Count()}");
            foreach (string detail in byStatus.Select(o => o.Detail).Where(d => d.Length > 0).Distinct().Take(3))
            {
                report.AppendLine($"    {detail}");
            }
        }

        report.AppendLine($"outbox rows whose dispatch failed on pool exhaustion: {await PoolExhaustedDispatchesAsync()}  " +
                          $"transactions_outbox still pending: {await ScalarAsync("SELECT count(*) FROM transactions_outbox_messages WHERE processed_at IS NULL")}");
        report.AppendLine($"relay errors: {relayErrors.Count}");
        foreach (string error in relayErrors.Distinct().Take(5))
        {
            report.AppendLine($"  {error}");
        }

        return report.ToString();
    }

    private Task<long> PoolExhaustedDispatchesAsync() => ScalarAsync(
        "SELECT (SELECT count(*) FROM bookings_outbox_messages WHERE last_error ILIKE '%pool has been exhausted%') + " +
        "(SELECT count(*) FROM transactions_outbox_messages WHERE last_error ILIKE '%pool has been exhausted%')");

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private async Task<long> PendingBookingsRowsAsync()
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using NpgsqlCommand command = new NpgsqlCommand(
            "SELECT count(*) FROM bookings_outbox_messages WHERE processed_at IS NULL AND dead_lettered_at IS NULL", connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static double Percentile(IEnumerable<Outcome> outcomes, double p)
    {
        List<double> sorted = outcomes.Select(o => o.Ms).OrderBy(ms => ms).ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
    }
}
