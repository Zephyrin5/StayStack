using System.Data;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Runs one unit of work as a single database transaction, retried by the context's execution
///     strategy (docs/adr/0025).
///     <para>
///         The work is the unit of retry, so everything it saves is built inside it and any identity a
///         retry must recognise is minted before the call. Each attempt starts from a cleared change
///         tracker, and an attempt that does not commit leaves none behind: entities saved and rolled
///         back read as Unchanged while describing rows that do not exist.
///     </para>
///     <para>
///         A contract implementation called inside the work saves its own changes before returning. The
///         runner refuses to commit while anything is unsaved, so a forgotten save fails loudly.
///     </para>
/// </summary>
public interface ITransactionRunner
{
    Task<T> ExecuteAsync<T>(
        IsolationLevel isolation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    Task ExecuteAsync(IsolationLevel isolation, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}
