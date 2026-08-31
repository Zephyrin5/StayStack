namespace Identity.Outbox;

// The compensation for BecomeHostHandler's own Hosts-side write
// (IHostRegistrar.RegisterHostAsync) when the follow-up Identity-side write
// (linking HostId, adding the Host role) fails - see docs/adr/0003.
public record DeleteHostOutboxMessage(Guid HostId);
