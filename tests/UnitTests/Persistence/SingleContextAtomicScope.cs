using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace UnitTests.Persistence;

// For unit tests of a handler whose collaborators in other modules are mocks: one
// SQLite context, one transaction, no retries. It proves nothing about
// participation or retry; AtomicScopeTests does, against Postgres.
public sealed class SingleContextAtomicScope(DbContext context) : IAtomicScope
{
    public async Task<T> ExecuteAsync<T>(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        T result = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task ExecuteAsync(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(owner, participants, async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);
}
