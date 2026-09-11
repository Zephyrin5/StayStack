using Outbox;
namespace Identity.Outbox;

// The compensation for BecomeHostHandler's own Hosts-side write
// (IHostRegistrar.RegisterHostAsync) when the follow-up Identity-side write
// (linking HostId, adding the Host role) fails - see docs/adr/0003.
public record DeleteHostOutboxMessage(Guid HostId) : IOutboxMessage
{
    // Persisted into rows that outlive the deployment that wrote them.
    // Renaming the record is free; changing this is not. See IOutboxMessage.
    public const string TypeName = "identity.delete-host.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}
