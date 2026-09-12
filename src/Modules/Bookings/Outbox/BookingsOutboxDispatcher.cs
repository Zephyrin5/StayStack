using Bookings.Contracts;
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
            case ReleaseHoldOutboxMessage.TypeName:
            {
                ReleaseHoldOutboxMessage payload = JsonSerializer.Deserialize(
                                                        message.Payload, BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage)
                                                    ?? throw new InvalidOperationException(
                                                        $"Outbox message {message.Id} had a null {nameof(ReleaseHoldOutboxMessage)} payload.");

                await holdConfirmation.ReleaseHoldAsync(payload.HoldId, cancellationToken);
                break;
            }

            case ReverseTransactionOutboxMessage.TypeName:
            {
                ReverseTransactionOutboxMessage payload = JsonSerializer.Deserialize(
                                                               message.Payload, BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage)
                                                           ?? throw new InvalidOperationException(
                                                               $"Outbox message {message.Id} had a null {nameof(ReverseTransactionOutboxMessage)} payload.");

                // A latency optimisation over ResolveOutstandingRefundsJob,
                // not the thing correctness rests on - the obligation is the
                // durable work item and this just asks for it to be settled
                // now. Safe to deliver twice, or never.
                await transactionReversal.ResolveRefundAsync(payload.BookingId, cancellationToken);
                break;
            }

            case ReverseRedemptionOutboxMessage.TypeName:
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
