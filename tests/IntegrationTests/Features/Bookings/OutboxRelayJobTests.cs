using Availability;
using Availability.Entities;
using Bookings;
using Bookings.Jobs;
using Bookings.Outbox;
using Bookings.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Text.Json;
namespace IntegrationTests.Features.Bookings;

// Exercises OutboxRelayJob against real, DI-resolved Contracts
// implementations (not mocks) - proves the actual wiring (DbContext ->
// dispatcher -> Availability.Contracts.IHoldConfirmation -> the real hold
// row) works end to end, not just that BookingsOutboxDispatcher's own
// switch statement compiles. See docs/adr/0003.
[Collection("Integration Tests")]
public class OutboxRelayJobTests(IntegrationTestWebApplicationFactory factory)
{
    private static UnitAvailabilityHold CreateBookedHold()
    {
        DateOnly checkIn = CatalogSeeding.Today();

        return new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Status = "booked",
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
            BookedAt = DateTimeOffset.UtcNow,
            TotalPrice = Money.Of(200m, Currency.KWD),
            Subtotal = 200m
        };
    }

    private async Task SeedHoldAsync(UnitAvailabilityHold hold)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        context.Add(hold);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        UnitAvailabilityHold hold = await context.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
        return hold.Status;
    }

    private async Task<Guid> SeedUnprocessedReleaseHoldMessageAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        OutboxMessage message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = nameof(ReleaseHoldOutboxMessage),
            Payload = JsonSerializer.Serialize(
                new ReleaseHoldOutboxMessage(holdId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage),
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        };
        context.BookingsOutboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return message.Id;
    }

    private async Task<Guid> SeedDeadLetteredReleaseHoldMessageAsync(Guid holdId, DateTimeOffset deadLetteredAt)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        OutboxMessage message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = nameof(ReleaseHoldOutboxMessage),
            Payload = JsonSerializer.Serialize(
                new ReleaseHoldOutboxMessage(holdId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage),
            CreatedAt = deadLetteredAt.AddHours(-1),
            NextAttemptAt = deadLetteredAt,
            Attempts = 10,
            LastError = "Simulated persistent failure.",
            DeadLetteredAt = deadLetteredAt
        };
        context.BookingsOutboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return message.Id;
    }

    private async Task<OutboxMessage> GetMessageAsync(Guid messageId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        return await context.BookingsOutboxMessages.AsNoTracking().SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
    }

    private async Task RunRelayAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        BookingsOutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>();
        OutboxRelayJob job = new OutboxRelayJob(dispatcher);
        await job.RelayAsync(null!, TestContext.Current.CancellationToken);
    }

    private async Task RunDeadLetterSweepAsync()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        BookingsOutboxDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>();
        OutboxRelayJob job = new OutboxRelayJob(dispatcher);
        await job.SweepDeadLetteredAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RelayAsync_DispatchesAndMarksProcessed_AnUnprocessedMessage()
    {
        UnitAvailabilityHold hold = CreateBookedHold();
        await SeedHoldAsync(hold);
        Guid messageId = await SeedUnprocessedReleaseHoldMessageAsync(hold.Id);

        await RunRelayAsync();

        Assert.Equal("held", await GetHoldStatusAsync(hold.Id));

        OutboxMessage message = await GetMessageAsync(messageId);
        Assert.NotNull(message.ProcessedAt);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.DeadLetteredAt);
    }

    [Fact]
    public async Task RelayAsync_MarksProcessed_WhenTheUnderlyingActionWasAlreadyDone()
    {
        // ReleaseHoldAsync is documented idempotent - a no-op once the hold
        // is no longer 'booked'. Simulates the message being retried after
        // its target action already succeeded via some other path (e.g. the
        // inline dispatch attempt actually went through, but the process
        // died before this row's own ProcessedAt save committed).
        UnitAvailabilityHold hold = CreateBookedHold();
        hold.Status = "held";
        await SeedHoldAsync(hold);
        Guid messageId = await SeedUnprocessedReleaseHoldMessageAsync(hold.Id);

        await RunRelayAsync();

        Assert.Equal("held", await GetHoldStatusAsync(hold.Id));

        OutboxMessage message = await GetMessageAsync(messageId);
        Assert.NotNull(message.ProcessedAt);
    }

    [Fact]
    public async Task SweepDeadLetteredAsync_RetriesAndClearsAMessage_PastItsCooldown()
    {
        // The scenario docs/adr/0003's dead-letter-sweep section exists for:
        // a message that exhausted its fast retry loop and got dead-lettered
        // (e.g. Availability was down for an hour), but the underlying
        // action is fine to retry now that whatever was wrong has cleared -
        // replayed from its own original Payload, no reconciliation job
        // needed.
        UnitAvailabilityHold hold = CreateBookedHold();
        await SeedHoldAsync(hold);
        Guid messageId = await SeedDeadLetteredReleaseHoldMessageAsync(hold.Id, DateTimeOffset.UtcNow.AddHours(-2));

        await RunDeadLetterSweepAsync();

        Assert.Equal("held", await GetHoldStatusAsync(hold.Id));

        OutboxMessage message = await GetMessageAsync(messageId);
        Assert.NotNull(message.ProcessedAt);
        Assert.Null(message.DeadLetteredAt);
    }

    [Fact]
    public async Task SweepDeadLetteredAsync_LeavesAMessage_StillWithinItsCooldown_Untouched()
    {
        UnitAvailabilityHold hold = CreateBookedHold();
        await SeedHoldAsync(hold);
        // Dead-lettered 5 minutes ago - well inside the 1-hour cooldown
        // OutboxRelayJob's own TickerFunction uses.
        Guid messageId = await SeedDeadLetteredReleaseHoldMessageAsync(hold.Id, DateTimeOffset.UtcNow.AddMinutes(-5));

        await RunDeadLetterSweepAsync();

        // Untouched: still 'booked' (no retry happened), and still
        // dead-lettered (not silently cleared without an attempt).
        Assert.Equal("booked", await GetHoldStatusAsync(hold.Id));

        OutboxMessage message = await GetMessageAsync(messageId);
        Assert.Null(message.ProcessedAt);
        Assert.NotNull(message.DeadLetteredAt);
    }
}
