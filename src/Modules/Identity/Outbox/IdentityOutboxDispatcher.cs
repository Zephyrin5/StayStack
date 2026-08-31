using Hosts.Contracts;
using Identity.Serialization;
using Microsoft.Extensions.Logging;
using Outbox;
using System.Text.Json;
namespace Identity.Outbox;

public class IdentityOutboxDispatcher(
    AppIdentityDbContext dbContext,
    IHostRegistrar hostRegistrar,
    TimeProvider timeProvider,
    ILogger<IdentityOutboxDispatcher> logger)
    : OutboxDispatcherBase<AppIdentityDbContext>(dbContext, timeProvider, logger)
{
    protected override string ModuleName => "Identity";

    protected override async Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case nameof(DeleteHostOutboxMessage):
            {
                DeleteHostOutboxMessage payload = JsonSerializer.Deserialize(
                                                       message.Payload, IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage)
                                                   ?? throw new InvalidOperationException(
                                                       $"Outbox message {message.Id} had a null {nameof(DeleteHostOutboxMessage)} payload.");

                await hostRegistrar.DeleteAsync(payload.HostId, cancellationToken);
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown Identity outbox message type '{message.Type}'.");
        }
    }
}
