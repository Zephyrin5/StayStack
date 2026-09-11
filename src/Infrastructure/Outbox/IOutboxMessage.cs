namespace Outbox;

/// <summary>
///     A message type that can name itself in a way that survives a rename.
///     <para>
///         <see cref="OutboxMessage.Type"/> used to be
///         <c>typeof(TMessage).Name</c>, and each dispatcher switched on those
///         strings. That makes a CLR type name a persisted identifier written
///         into rows that outlive the deployment that wrote them: rename the
///         record after a release and every undelivered row of that type stops
///         routing - dead-lettered, one per booking or payment it was
///         compensating - with nothing in the rename to warn anyone. An IDE
///         refactor is exactly the sort of change nobody thinks to check, and
///         the breakage arrives later, on the messages that mattered enough to
///         still be retrying.
///     </para>
///     <para>
///         An interface rather than a parameter on <c>Enqueue</c>, so a new
///         message type cannot be added without choosing an identifier - and
///         <c>static abstract</c> rather than an instance member, so the
///         dispatcher can read it generically without a message in hand.
///     </para>
///     <para>
///         <b>These strings are a wire format.</b> The <c>.v1</c> suffix is
///         what makes a payload shape changeable later: publish
///         <c>…-payment.v2</c> alongside, keep the <c>v1</c> case routing until
///         no rows carry it, and then delete it. Changing an existing
///         identifier in place is the very thing this exists to prevent.
///     </para>
/// </summary>
public interface IOutboxMessage
{
    /// <summary>
    ///     The stable identifier persisted in <see cref="OutboxMessage.Type"/>,
    ///     conventionally <c>module.what-it-does.vN</c>. Implemented explicitly
    ///     by each message, forwarding to a <c>public const string TypeName</c>
    ///     the dispatchers can use as a <c>switch</c> label.
    /// </summary>
    static abstract string OutboxType { get; }
}
