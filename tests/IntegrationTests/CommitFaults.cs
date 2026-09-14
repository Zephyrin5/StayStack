using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data.Common;
namespace IntegrationTests;

/// <summary>
///     The only way a test here injects a failure around a commit - and it offers
///     exactly two, named for what they mean.
///     <para>
///         <see cref="FailAfterCommit{TContext}"/> is a lost acknowledgement: the
///         work is durable and the caller is told it is not. <see
///         cref="FailBeforeCommit{TContext}"/> is a failed commit: nothing was
///         written. An execution strategy retries both identically, which is why
///         the difference is invisible from inside a test - and six tests in this
///         suite ended up on the wrong side of it, most of them silently, when an
///         explicit transaction was added around the code they exercised and a
///         hook that used to follow the commit started preceding it.
///     </para>
///     <para>
///         So no test names an interceptor. The hook lives here, and the entry
///         point a test calls is its statement of which case it proves.
///         RetryFaultInjectionProtocolTests fails any test file that reaches for
///         a transaction or command interceptor directly.
///     </para>
///     <para>
///         Every fault is targeted. Test hosts run TickerQ, whose jobs commit on
///         their own schedule, and an untargeted fault lets one of those take the
///         injection: the request then runs clean, the fault reports that it
///         fired, and the test passes having proved nothing. That was
///         demonstrated, not supposed. <c>when</c> must identify the commit under
///         test.
///     </para>
/// </summary>
public static class CommitFaults
{
    /// <summary>
    ///     Throws a transient error (40001) after a matching commit has landed.
    ///     <paramref name="when"/> runs after the commit, so it can read durable
    ///     state - see <see cref="CommittedRowExistsAsync"/> for writes made
    ///     through Dapper, which leave nothing in the change tracker.
    /// </summary>
    public static CommitFault<TContext> FailAfterCommit<TContext>(
        Func<TContext, CancellationToken, Task<bool>> when, int times = 1)
        where TContext : DbContext =>
        new CommitFault<TContext>(afterCommit: true, when, times);

    /// <inheritdoc cref="FailAfterCommit{TContext}(Func{TContext, CancellationToken, Task{bool}}, int)"/>
    public static CommitFault<TContext> FailAfterCommit<TContext>(Func<TContext, bool> when, int times = 1)
        where TContext : DbContext =>
        new CommitFault<TContext>(afterCommit: true, (context, _) => Task.FromResult(when(context)), times);

    /// <summary>
    ///     Throws a transient error (40001) as a matching commit is about to run,
    ///     so the transaction rolls back. <paramref name="when"/> sees the change
    ///     tracker only - nothing is durable yet.
    /// </summary>
    public static CommitFault<TContext> FailBeforeCommit<TContext>(Func<TContext, bool> when, int times = 1)
        where TContext : DbContext =>
        new CommitFault<TContext>(afterCommit: false, (context, _) => Task.FromResult(when(context)), times);

    /// <summary>A host whose <typeparamref name="TContext"/> carries the fault.</summary>
    public static WebApplicationFactory<Program> WithCommitFault<TContext>(
        this WebApplicationFactory<Program> factory, CommitFault<TContext> fault)
        where TContext : DbContext =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureDbContext<TContext>(options => options.AddInterceptors(fault.Interceptor))));

    /// <summary>
    ///     Whether a committed row matches <paramref name="existsSql"/>, read on a
    ///     separate connection - for targeting a commit whose write went through
    ///     Dapper. Only meaningful after the commit.
    /// </summary>
    public static async Task<bool> CommittedRowExistsAsync(
        DbContext context, string existsSql, string parameterName, object parameterValue,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection probe = new NpgsqlConnection(context.Database.GetConnectionString());
        await probe.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = new NpgsqlCommand($"SELECT EXISTS ({existsSql})", probe);
        command.Parameters.AddWithValue(parameterName, parameterValue);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

/// <summary>A targeted commit fault. Created through <see cref="CommitFaults"/>.</summary>
public sealed class CommitFault<TContext> where TContext : DbContext
{
    private readonly bool _afterCommit;
    private readonly Func<TContext, CancellationToken, Task<bool>> _when;
    private readonly int _times;
    private int _armed = 1;
    private int _fired;

    internal CommitFault(bool afterCommit, Func<TContext, CancellationToken, Task<bool>> when, int times)
    {
        _afterCommit = afterCommit;
        _when = when;
        _times = times;
        Interceptor = new Hook(this);
    }

    internal IInterceptor Interceptor { get; }

    /// <summary>How many times it has thrown.</summary>
    public int Fired => Volatile.Read(ref _fired);

    /// <summary>
    ///     Assert this. A fault that never reached the commit under test leaves
    ///     every other assertion passing for the wrong reason.
    /// </summary>
    public bool HasFired => Fired > 0;

    /// <summary>Faults start armed; disarm around setup that would match.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    public void Arm() => Volatile.Write(ref _armed, 1);

    private async Task ThrowIfMatchesAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _armed) == 0
            || context is not TContext typed
            || Volatile.Read(ref _fired) >= _times
            || !await _when(typed, cancellationToken)
            || Interlocked.Increment(ref _fired) > _times)
        {
            return;
        }

        throw new PostgresException(
            _afterCommit ? "simulated lost acknowledgement after commit" : "simulated transient failure before commit",
            "ERROR", "ERROR", "40001");
    }

    private sealed class Hook(CommitFault<TContext> fault) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!fault._afterCommit)
            {
                await fault.ThrowIfMatchesAsync(eventData.Context, cancellationToken);
            }

            return result;
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (fault._afterCommit)
            {
                await fault.ThrowIfMatchesAsync(eventData.Context, cancellationToken);
            }
        }
    }
}
