using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
namespace IntegrationTests.Measurements;

/// <summary>
///     Counts pooled connections held open at once by one logical operation.
///     <para>
///         Subscribes to each DbConnection's StateChange when EF creates it, so
///         an open counts whoever issued it - EF, or Dapper auto-opening the
///         context's connection. The operation is identified by an AsyncLocal
///         the measuring test sets around its call; the test server must
///         preserve the execution context for it to reach request handlers.
///         Connections opened by background jobs carry no operation and are not
///         counted.
///     </para>
/// </summary>
public sealed class ConnectionMeter : DbConnectionInterceptor
{
    private static readonly AsyncLocal<Operation?> Current = new();

    public sealed class Operation(string name)
    {
        private readonly ConcurrentDictionary<DbConnection, string> _open = new();
        private int _peak;

        public string Name { get; } = name;
        public int Peak => _peak;
        public int Opens { get; private set; }
        public ConcurrentBag<string> PeakContexts { get; } = [];

        internal void Opened(DbConnection connection, string context)
        {
            lock (this)
            {
                _open[connection] = context;
                Opens++;
                if (_open.Count > _peak)
                {
                    _peak = _open.Count;
                    PeakContexts.Clear();
                    foreach (string c in _open.Values)
                    {
                        PeakContexts.Add(c);
                    }
                }
            }
        }

        internal void Closed(DbConnection connection)
        {
            lock (this)
            {
                _open.TryRemove(connection, out _);
            }
        }
    }

    public static Operation Begin(string name)
    {
        Operation operation = new Operation(name);
        Current.Value = operation;
        return operation;
    }

    public static void End() => Current.Value = null;

    public override DbConnection ConnectionCreated(ConnectionCreatedEventData eventData, DbConnection result)
    {
        string context = eventData.Context?.GetType().Name ?? "unknown";
        result.StateChange += (sender, args) =>
        {
            Operation? operation = Current.Value;
            if (operation is null || sender is not DbConnection connection)
            {
                return;
            }

            if (args.CurrentState == ConnectionState.Open && args.OriginalState != ConnectionState.Open)
            {
                operation.Opened(connection, context);
            }
            else if (args.CurrentState != ConnectionState.Open && args.OriginalState == ConnectionState.Open)
            {
                operation.Closed(connection);
            }
        };

        return result;
    }
}
