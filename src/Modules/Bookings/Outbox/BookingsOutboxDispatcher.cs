using Availability.Contracts;
using Bookings.Serialization;
using Microsoft.Extensions.Logging;
using Outbox;
using Promotions.Contracts;
using SeedWork.ValueObjects;
using System.Text.Json;
using Transactions.Contracts;
namespace Bookings.Outbox;

public class BookingsOutboxDispatcher(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    ITransactionReversal transactionReversal,
    IPromotionRedemption promotionRedemption,
    TimeProvider timeProvider,
    ILogger<BookingsOutboxDispatcher> logger)
    : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, timeProvider, logger)
{
    protected override string ModuleName => "Bookings";

    protected override async Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case nameof(ReleaseHoldOutboxMessage):
            {
                ReleaseHoldOutboxMessage payload = JsonSerializer.Deserialize(
                                                        message.Payload, BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage)
                                                    ?? throw new InvalidOperationException(
                                                        $"Outbox message {message.Id} had a null {nameof(ReleaseHoldOutboxMessage)} payload.");

                await holdConfirmation.ReleaseHoldAsync(payload.HoldId, cancellationToken);
                break;
            }

            case nameof(ReverseTransactionOutboxMessage):
            {
                ReverseTransactionOutboxMessage payload = JsonSerializer.Deserialize(
                                                               message.Payload, BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage)
                                                           ?? throw new InvalidOperationException(
                                                               $"Outbox message {message.Id} had a null {nameof(ReverseTransactionOutboxMessage)} payload.");

                await transactionReversal.ReverseTransactionAsync(
                    payload.BookingId,
                    Money.Of(payload.RefundAmount, payload.Currency),
                    cancellationToken);
                break;
            }

            case nameof(ReverseRedemptionOutboxMessage):
            {
                ReverseRedemptionOutboxMessage payload = JsonSerializer.Deserialize(
                                                              message.Payload, BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage)
                                                          ?? throw new InvalidOperationException(
                                                              $"Outbox message {message.Id} had a null {nameof(ReverseRedemptionOutboxMessage)} payload.");

                await promotionRedemption.ReverseRedemptionAsync(payload.BookingId, cancellationToken);
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown Bookings outbox message type '{message.Type}'.");
        }
    }
}
