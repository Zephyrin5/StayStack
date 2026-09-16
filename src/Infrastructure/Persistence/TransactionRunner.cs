using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
namespace Persistence;

internal sealed class TransactionRunner(AppDbContext db) : ITransactionRunner
{
    public async Task<T> ExecuteAsync<T>(
        IsolationLevel isolation, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            bool committed = false;

            try
            {
                await using IDbContextTransaction transaction =
                    await db.Database.BeginTransactionAsync(isolation, cancellationToken);

                T result = await work(cancellationToken);

                if (db.ChangeTracker.HasChanges())
                {
                    throw new InvalidOperationException(
                        "The work left unsaved changes. Whatever wrote them must save before returning, or they " +
                        "would be dropped silently when this transaction commits.");
                }

                await transaction.CommitAsync(cancellationToken);
                committed = true;

                return result;
            }
            finally
            {
                if (!committed)
                {
                    db.ChangeTracker.Clear();
                }
            }
        });
    }

    public async Task ExecuteAsync(
        IsolationLevel isolation, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        await ExecuteAsync<bool>(isolation, async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);
}
