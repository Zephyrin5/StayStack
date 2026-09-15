// Proves, against the live migrated schema, the two database facts CommittedInsertRecovery's
// constraint-name match depends on; see the header for why the second is not a formality.
using Catalog;
using Catalog.Entities;
using Hosts;
using Hosts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Persistence;
using Promotions;
using Promotions.Entities;
using Reviews;
using Reviews.Entities;
namespace IntegrationTests.Features.Common;

// The create handlers recover an insert that committed and lost its
// acknowledgement by matching a unique violation against the entity's own
// primary key name, taken from the EF model. Two things about the database make
// that match correct, and neither is visible from the handler:
//
// 1. The primary key constraint carries the model's name. A migration that
//    renamed it would make the match silently stop, and the handler would go
//    back to answering an error for a row that exists.
//
// 2. The primary key is the table's oldest unique index. A retried insert of our
//    own row violates the primary key AND every other unique index at once, and
//    Postgres reports only the first one it checks - in index OID order. Probed:
//    with a unique index created before the primary key, the same duplicate row
//    is reported under that index's name, not the key's. Here that would turn
//    "our review is already there" into "already reviewed", the confident wrong
//    answer the recovery exists to remove. A migration that ever recreates one
//    of these primary keys would flip it.
[Collection("Integration Tests")]
public class PrimaryKeyConstraintTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed record Row(string Name, bool IsFirstUniqueIndex);

    public static TheoryData<string> RecoveredTables => ["properties", "promotions", "guest_reviews", "stay_reviews", "hosts"];

    private static (DbContext Context, string ModelName) Resolve(IServiceProvider services, string table) => table switch
    {
        "properties" => Model<AppCatalogDbContext, Property>(services),
        "promotions" => Model<AppPromotionsDbContext, Promotion>(services),
        "guest_reviews" => Model<AppReviewsDbContext, GuestReview>(services),
        "stay_reviews" => Model<AppReviewsDbContext, StayReview>(services),
        "hosts" => Model<AppHostsDbContext, Host>(services),
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    private static (DbContext, string) Model<TContext, TEntity>(IServiceProvider services) where TContext : DbContext
    {
        TContext context = services.GetRequiredService<TContext>();
        return (context, ConstraintViolations.PrimaryKeyNameOf<TEntity>(context));
    }

    [Theory]
    [MemberData(nameof(RecoveredTables))]
    public async Task ThePrimaryKey_CarriesTheModelsName_AndIsCheckedBeforeAnyOtherUniqueIndex(string table)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        (DbContext context, string modelName) = Resolve(scope.ServiceProvider, table);

        Row row = Assert.Single(await context.Database.SqlQuery<Row>($"""
            SELECT con.conname AS name,
                   con.conindid::text::bigint = (
                       SELECT min(i.indexrelid::text::bigint)
                       FROM pg_index i
                       WHERE i.indrelid = con.conrelid AND i.indisunique) AS is_first_unique_index
            FROM pg_constraint con
            JOIN pg_class t ON t.oid = con.conrelid
            WHERE con.contype = 'p' AND t.relname = {table}
            """).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(modelName, row.Name);
        Assert.True(row.IsFirstUniqueIndex,
            $"{row.Name} is not the oldest unique index on {table}, so a re-inserted row is reported under another " +
            "index's name and CommittedInsertRecovery answers a domain error for this operation's own committed row.");
    }
}
