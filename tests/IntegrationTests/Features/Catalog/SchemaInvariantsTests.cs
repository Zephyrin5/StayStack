using Catalog;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
namespace IntegrationTests.Features.Catalog;

// The double-booking guard on unit_availability_holds is a GIST exclusion
// constraint - Npgsql's EF Core provider has no fluent API for these
// (confirmed against a still-open, ~5-year-stale upstream feature request:
// npgsql/efcore.pg#1975), so unlike every other constraint in this schema
// it exists only as hand-written SQL inside the Initial migration's Up(),
// with no representation in the C# model at all. That means a migration
// squash/regenerate from the current model would silently omit it - nothing
// else would ever catch that. This test is the catch: it asserts the
// constraint actually exists in the live schema, so losing it fails CI
// immediately instead of quietly reopening the double-booking bug the
// constraint exists to prevent.
[Collection("Integration Tests")]
public class SchemaInvariantsTests(IntegrationTestWebApplicationFactory factory)
{
    [Fact]
    public async Task UnitAvailabilityHolds_ShouldHaveOverlapExclusionConstraint()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        IDbConnection connection = context.Database.GetDbConnection();

        const string sql = """
                            SELECT conname
                            FROM pg_constraint
                            WHERE conname = 'unit_availability_holds_overlap_excl' AND contype = 'x';
                            """;

        string? constraintName = await connection.QueryFirstOrDefaultAsync<string>(sql);

        Assert.Equal("unit_availability_holds_overlap_excl", constraintName);
    }
}
