using Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Persistence;
using Testcontainers.PostgreSql;
using TickerQ.EntityFrameworkCore.DbContextFactory;
namespace IntegrationTests;

/// <summary>
///     One container and one set of migrations for the whole assembly, and a database per collection
///     copied from them.
///     <para>
///         Collections run in parallel, so they cannot share a database: one test's seed row is
///         another's unexpected result, and a commit fault targeted at a request can be taken by a
///         commit from an unrelated test. Migrating per collection instead would pay for the schema
///         once per collection, which is most of what a run costs.
///     </para>
///     <para>
///         So: migrate once into a template, then <c>CREATE DATABASE ... TEMPLATE</c>, which copies
///         the files rather than replaying the migrations. Postgres refuses to copy a template while
///         anything is connected to it, which is why the migration connections are disposed and the
///         pool cleared before any collection asks for a database.
///     </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "staystack_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("staystack_admin")
        .WithUsername("postgres")
        .WithPassword("postgres")
        // One server now serves every collection at once, each host with a pool of its own. The
        // default 100 is a ceiling the suite can reach with nothing wrong, and it arrives as a
        // refused connection in whichever test happened to be last.
        .WithCommand("-c", "max_connections=300")
        .Build();

    // CREATE DATABASE cannot run inside a transaction and fails while another copy of the same
    // template is in progress, so collections queue here rather than racing.
    private readonly SemaphoreSlim _creating = new SemaphoreSlim(1, 1);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await ExecuteOnAdminAsync($"CREATE DATABASE \"{TemplateDatabase}\";");
        await MigrateAsync<AppDbContext>(TemplateDatabase, "app", "Database");
        await MigrateAsync<TickerQDbContext>(TemplateDatabase, "jobs", "Jobs");

        // The migration connections are gone, but Npgsql keeps them pooled, and a pooled connection
        // is still a connection as far as CREATE DATABASE ... TEMPLATE is concerned.
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        _creating.Dispose();
        await _container.StopAsync();
    }

    /// <summary>
    ///     A database of this collection's own, with the schema already in it. The name is the
    ///     collection's, so a failure mid-run leaves something a person can open and read.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await _creating.WaitAsync();

        try
        {
            await ExecuteOnAdminAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\";");
        }
        finally
        {
            _creating.Release();
        }

        return ConnectionStringFor(name);
    }

    /// <summary>The connection string for a database on this container, without creating it.</summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    private async Task ExecuteOnAdminAsync(string sql)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        await using NpgsqlCommand command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // The module name selects the migrations-history table, so it has to match what the application's
    // own registration passes - a mismatch silently re-runs every migration into the wrong bookkeeping.
    private async Task MigrateAsync<TContext>(string database, string moduleName, string migrationsAssembly)
        where TContext : DbContext
    {
        DbContextOptionsBuilder<TContext> builder = new DbContextOptionsBuilder<TContext>();
        builder.ConfigureStayStackDefaults(
            ConnectionStringFor(database), moduleName, isDevelopment: false, migrationsAssembly);

        // AppDbContext takes the module models as well; every other context here is a plain EF
        // context with the standard constructor, and the first test fails loudly if one is not.
        await using TContext context = typeof(TContext) == typeof(AppDbContext)
            ? (TContext)(object)new AppDbContext(
                (DbContextOptions<AppDbContext>)(object)builder.Options, AppDbContextModels.All)
            : (TContext)Activator.CreateInstance(typeof(TContext), builder.Options)!;

        await context.Database.MigrateAsync();
    }
}
