using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Data;
using System.Data.Common;
namespace Persistence;

/// <summary>One module's context, registered against its flag.</summary>
public sealed record AtomicParticipantRegistration(AtomicParticipants Participant, Type ContextType);

/// <summary>The <see cref="IAtomicScope"/> implementation; the contract is on the interface.</summary>
internal sealed class AtomicScope(
    IServiceProvider services,
    IEnumerable<AtomicParticipantRegistration> registrations) : IAtomicScope
{
    public async Task ExecuteAsync(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken) =>
        await ExecuteAsync<bool>(owner, participants, async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);

    public async Task<T> ExecuteAsync<T>(
        AtomicParticipants owner,
        AtomicParticipants participants,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (owner == AtomicParticipants.None || (owner & (owner - 1)) != 0)
        {
            throw new ArgumentException($"The owner must be exactly one participant, not '{owner}'.", nameof(owner));
        }

        DbContext ownerContext = Resolve(owner);
        List<Borrower> borrowers = Flags(participants & ~owner)
            .Select(flag => new Borrower(Resolve(flag)))
            .ToList();

        EnsureIdle(ownerContext);
        foreach (Borrower borrower in borrowers)
        {
            EnsureIdle(borrower.Context);
        }

        IExecutionStrategy strategy = ownerContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            ownerContext.ChangeTracker.Clear();
            foreach (Borrower borrower in borrowers)
            {
                borrower.Context.ChangeTracker.Clear();
            }

            bool committed = false;

            try
            {
                await using IDbContextTransaction transaction =
                    await ownerContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

                DbConnection connection = ownerContext.Database.GetDbConnection();
                foreach (Borrower borrower in borrowers)
                {
                    await borrower.EnlistAsync(connection, transaction.GetDbTransaction(), cancellationToken);
                }

                T result = await work(cancellationToken);

                EnsureSaved(ownerContext);
                foreach (Borrower borrower in borrowers)
                {
                    EnsureSaved(borrower.Context);
                }

                await transaction.CommitAsync(cancellationToken);
                committed = true;
                return result;
            }
            finally
            {
                // After the transaction is disposed, so the shared connection the
                // owner opened is already closed when each borrower lets go of it.
                foreach (Borrower borrower in borrowers)
                {
                    await borrower.ReleaseAsync();
                }

                // Saved-then-rolled-back entities read as Unchanged, so nothing
                // else would reveal that they describe rows that do not exist.
                if (!committed)
                {
                    ownerContext.ChangeTracker.Clear();
                    foreach (Borrower borrower in borrowers)
                    {
                        borrower.Context.ChangeTracker.Clear();
                    }
                }
            }
        });
    }

    private DbContext Resolve(AtomicParticipants participant)
    {
        AtomicParticipantRegistration registration = registrations.SingleOrDefault(r => r.Participant == participant)
                                                     ?? throw new InvalidOperationException(
                                                         $"No context is registered for atomic participant '{participant}'.");

        return (DbContext)services.GetRequiredService(registration.ContextType);
    }

    private static IEnumerable<AtomicParticipants> Flags(AtomicParticipants participants) =>
        Enum.GetValues<AtomicParticipants>().Where(flag => flag != AtomicParticipants.None && participants.HasFlag(flag));

    private static void EnsureIdle(DbContext context)
    {
        string problem =
            context.Database.CurrentTransaction is not null ? "is already in a transaction"
            : context.Database.GetDbConnection().State != ConnectionState.Closed ? "has an open connection"
            : context.ChangeTracker.HasChanges() ? "has unsaved tracked changes, which the scope would discard"
            : "";

        if (problem.Length > 0)
        {
            throw new InvalidOperationException(
                $"{context.GetType().Name} {problem}, so it cannot join an atomic scope. Open the scope before " +
                "this context is used for writes in the request, and do not nest scopes over the same context.");
        }
    }

    private static void EnsureSaved(DbContext context)
    {
        if (context.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                $"{context.GetType().Name} has unsaved changes at commit. Save through each participant inside the work; " +
                "the scope does not save on its behalf.");
        }
    }

    private sealed class Borrower(DbContext context)
    {
        private readonly string? _connectionString = context.Database.GetConnectionString();
        private bool _enlisted;

        public DbContext Context { get; } = context;

        public async Task EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            Context.Database.SetDbConnection(connection, contextOwnsConnection: false);
            _enlisted = true;
            await Context.Database.UseTransactionAsync(transaction, cancellationToken);
        }

        public async Task ReleaseAsync()
        {
            if (!_enlisted)
            {
                return;
            }

            // Not the caller's token: release must run even when the work was
            // cancelled, or the context stays pointed at a connection it does not own.
            await Context.Database.UseTransactionAsync(null, CancellationToken.None);
            Context.Database.SetDbConnection(null);
            Context.Database.SetConnectionString(_connectionString);
            _enlisted = false;
        }
    }
}

public static class AtomicScopeServicesRegistration
{
    /// <summary>
    ///     Registers <typeparamref name="TContext"/> as this module's atomic
    ///     participant. The context must be registered with AddDbContext, never
    ///     pooled: a pooled context returned with a borrowed connection would carry
    ///     it into the next request (DbContextRegistrationProtocolTests).
    /// </summary>
    public static IServiceCollection AddAtomicParticipant<TContext>(
        this IServiceCollection services, AtomicParticipants participant)
        where TContext : DbContext
    {
        if (participant == AtomicParticipants.None || (participant & (participant - 1)) != 0)
        {
            throw new ArgumentException($"Register exactly one participant, not '{participant}'.", nameof(participant));
        }

        services.AddSingleton(new AtomicParticipantRegistration(participant, typeof(TContext)));
        services.TryAddScoped<IAtomicScope, AtomicScope>();
        return services;
    }
}
