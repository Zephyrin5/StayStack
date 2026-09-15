namespace BuildingBlocks.Persistence;

/// <summary>
///     The module contexts a unit of work runs across. One flag per module, so a
///     workflow's participation is stated at the call site and greppable.
/// </summary>
[Flags]
public enum AtomicParticipants
{
    None = 0,
    Identity = 1 << 0,
    Catalog = 1 << 1,
    Hosts = 1 << 2,
    Promotions = 1 << 3,
    Bookings = 1 << 4,
    Transactions = 1 << 5,
    Reviews = 1 << 6
}

/// <summary>
///     Runs one unit of work across several modules' contexts as a single
///     Read Committed database transaction on a single pooled connection
///     (docs/design/transaction-ownership.md).
///     <para>
///         <b>Ownership.</b> The scope owns the transaction and the retry loop. It
///         opens both on <c>owner</c>, the calling module's context: that context's
///         execution strategy runs the work, and its connection and transaction are
///         the only ones used. Every other participant borrows them. Commit-fault
///         injection therefore targets the owner's context.
///     </para>
///     <para>
///         <b>Participation is explicit.</b> The caller names every participant. A
///         context not named is not enlisted and keeps its own connection. Each
///         module registers its context against its flag; a flag with no
///         registration throws.
///     </para>
///     <para>
///         <b>Participants do not own transactions.</b> A method that may run inside
///         a scope never calls <c>BeginTransactionAsync</c>, <c>CommitAsync</c> or
///         <c>RollbackAsync</c>; it uses and requires the ambient transaction
///         (<c>HoldConfirmation.RequiredTransaction</c> is the precedent). After a
///         database error it rethrows: Postgres aborts the transaction on a failed
///         statement, so nothing more can run in it, and the scope rolls back.
///     </para>
///     <para>
///         <b>Entry.</b> Every participant must be idle: no open connection, no
///         transaction, no unsaved tracked changes. A participant that is not throws
///         naming its context, rather than failing later inside EF. A context used
///         earlier in the request is idle, because EF closes its connection after
///         each operation. This also rejects nesting a scope inside a scope over the
///         same context.
///     </para>
///     <para>
///         <b>Attempts.</b> The work is the unit of retry. Each attempt clears every
///         participant's change tracker, begins the owner's transaction, and for
///         each other participant sets the owner's connection
///         (<c>contextOwnsConnection: false</c>) and enlists in the owner's
///         transaction. A rolled-back attempt's tracked entities never survive into
///         the next. Identity a retry must recognise is minted by the caller before
///         calling (docs/adr/0025). Operations on participants inside the work do
///         not retry on their own: EF suspends execution strategies inside a running
///         one.
///     </para>
///     <para>
///         <b>Dapper.</b> Use a participant's <c>Database.GetDbConnection()</c> and
///         <c>Database.CurrentTransaction.GetDbTransaction()</c>; both are the
///         owner's.
///     </para>
///     <para>
///         <b>Commit.</b> The work saves through each participant itself. Before
///         committing, the scope throws if any participant still has unsaved
///         changes, so a forgotten save fails instead of silently dropping writes.
///     </para>
///     <para>
///         <b>Release,</b> on every path including failure and cancellation: the
///         owner's transaction is disposed, which closes the connection it opened;
///         then each other participant leaves the transaction and gets its own
///         connection string back. The scope never disposes a connection. When
///         the scope does not commit, every participant's change tracker is cleared:
///         entities saved and rolled back read as Unchanged but describe rows that do
///         not exist.
///     </para>
///     <para>
///         <b>Not supported:</b> isolation other than Read Committed
///         (<c>HoldAvailabilityHandler</c> keeps its own Serializable transaction),
///         and enlisting a context the caller did not name.
///     </para>
/// </summary>
public interface IAtomicScope
{
    Task<T> ExecuteAsync<T>(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);
}
