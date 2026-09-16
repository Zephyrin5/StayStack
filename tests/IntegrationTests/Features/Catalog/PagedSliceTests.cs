using System.Data.Common;
using BuildingBlocks.Pagination;
using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Persistence;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Catalog;

// ToPagedListAsync issues a CountAsync beside the page query, and the count
// runs the full WHERE clause with no LIMIT to bound it. On GetProperties -
// an ILIKE plus an EXISTS over a candidate-unit id array - that is the
// expensive filter executed twice per cache miss to produce a number the
// browse UI only ever compares against a running total to decide whether to
// fetch more.
//
// ToPagedSliceAsync answers that question from the page query itself by
// asking for one row past the page. These assert the two things that claim
// rests on: that the extra round trip is genuinely gone, and that the probe
// row never escapes into the results.
[Collection("Integration Tests")]
public class PagedSliceTests(IntegrationTestWebApplicationFactory factory)
{
    // Counts round trips rather than inspecting SQL text: the claim is about
    // how many times the database is asked, which is the thing that costs.
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // The scope has to outlive the context, not just the options lookup: the
    // copied options carry the scope's IServiceProvider as their application
    // service provider, so disposing the scope first makes every query throw
    // ObjectDisposedException before it reaches the database - which looks
    // exactly like a failing assertion while proving nothing.
    private sealed class CountedContext(IServiceScope scope, AppDbContext db) : IAsyncDisposable
    {
        public CatalogDb Context { get; } = new CatalogDb(db);

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            scope.Dispose();
        }
    }

    // A separate context, because an interceptor has to be present when the
    // context is configured and the container's registered AppDbContext
    // already is. Built by copying the registered options rather than calling
    // UseNpgsql from scratch: the registration also applies the snake_case
    // naming convention, and a context configured without it looks for a
    // "Properties" table that does not exist.
    private CountedContext ContextWith(CommandCountingInterceptor interceptor)
    {
        IServiceScope scope = factory.Services.CreateScope();
        DbContextOptions<AppDbContext> registered =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        return new CountedContext(
            scope,
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>(registered)
                    .AddInterceptors(interceptor)
                    .Options,
                scope.ServiceProvider.GetRequiredService<IEnumerable<IModuleModel>>()));
    }

    [Fact]
    public async Task ToPagedSliceAsync_IssuesOneQuery_WhereToPagedListAsyncIssuesTwo()
    {
        CommandCountingInterceptor sliceInterceptor = new();
        CommandCountingInterceptor countedInterceptor = new();

        await using (CountedContext slice = ContextWith(sliceInterceptor))
        {
            await slice.Context.Properties.AsNoTracking().OrderBy(p => p.Id)
                .ToPagedSliceAsync(1, 5, TestContext.Current.CancellationToken);
        }

        await using (CountedContext counted = ContextWith(countedInterceptor))
        {
            await counted.Context.Properties.AsNoTracking().OrderBy(p => p.Id)
                .ToPagedListAsync(1, 5, TestContext.Current.CancellationToken);
        }

        // The point of asserting both in one test: 1 on its own would pass
        // against a slice implementation that silently stopped querying, and
        // proves nothing about what was saved. The difference is the finding.
        Assert.Equal(1, sliceInterceptor.Count);
        Assert.Equal(2, countedInterceptor.Count);
    }

    [Fact]
    public async Task ToPagedSliceAsync_ReportsNextPage_WithoutLeakingTheProbeRow()
    {
        // A city nothing else in the shared database uses, so the row count
        // under test is exactly what this test seeded - the same isolation
        // trick GetProperties_ShouldFilterByCity uses.
        string uniqueCity = $"City-{Guid.NewGuid():N}";
        await SeedPropertiesAsync(uniqueCity, 3);

        CommandCountingInterceptor interceptor = new();
        await using CountedContext counted = ContextWith(interceptor);

        IQueryable<Property> query = counted.Context.Properties.AsNoTracking()
            .Where(p => p.City == uniqueCity)
            .OrderBy(p => p.Id);

        (List<Property> page1, bool more1) =
            await query.ToPagedSliceAsync(1, 2, TestContext.Current.CancellationToken);
        (List<Property> page2, bool more2) =
            await query.ToPagedSliceAsync(2, 2, TestContext.Current.CancellationToken);

        // Exactly pageSize, not pageSize + 1 - the probe row is what makes
        // more1 true and must not be returned. Without the RemoveAt this is 3.
        Assert.Equal(2, page1.Count);
        Assert.True(more1);

        Assert.Single(page2);
        Assert.False(more2);

        // No overlap and no gap: the discarded probe row must still appear at
        // the head of the next page. Dropping it from the result without
        // re-fetching it would silently lose one property per page.
        Assert.Equal(3, page1.Concat(page2).Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task ToPagedSliceAsync_OnAnExactlyFullLastPage_ReportsNoNextPage()
    {
        // The boundary the probe exists to get right: pageSize divides the
        // total exactly, so the last page is full. "Full page means more to
        // come" - the naive rule this replaces - reports a next page here and
        // sends the client to fetch an empty one.
        string uniqueCity = $"City-{Guid.NewGuid():N}";
        await SeedPropertiesAsync(uniqueCity, 2);

        CommandCountingInterceptor interceptor = new();
        await using CountedContext counted = ContextWith(interceptor);

        (List<Property> page, bool hasNextPage) = await counted.Context.Properties.AsNoTracking()
            .Where(p => p.City == uniqueCity)
            .OrderBy(p => p.Id)
            .ToPagedSliceAsync(1, 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Count);
        Assert.False(hasNextPage);
    }

    [Fact]
    public async Task ToPagedSliceAsync_StillRejectsAnOutOfBoundsOffset()
    {
        // Skipping the count does not skip the offset - a slice query still
        // emits OFFSET, so it needs the same bound. Sharing the guard between
        // the two overloads is what this pins.
        CommandCountingInterceptor interceptor = new();
        await using CountedContext counted = ContextWith(interceptor);

        await Assert.ThrowsAsync<BuildingBlocks.Exceptions.ValidationException>(
            async () => await counted.Context.Properties.AsNoTracking().OrderBy(p => p.Id)
                .ToPagedSliceAsync(30_000_000, 100, TestContext.Current.CancellationToken));

        // Rejected before anything was asked of the database, which is the
        // reason the guard runs first.
        Assert.Equal(0, interceptor.Count);
    }

    [Fact]
    public async Task ToPagedSliceAsync_RejectsAPageSizePastTheMaximum()
    {
        // The offset guard alone never catches this: at page 1 the offset is
        // 0 whatever the page size, int.MaxValue included. This bound is what
        // gives a paged caller arriving without a validator a ceiling.
        CommandCountingInterceptor interceptor = new();
        await using CountedContext counted = ContextWith(interceptor);

        await Assert.ThrowsAsync<BuildingBlocks.Exceptions.ValidationException>(
            async () => await counted.Context.Properties.AsNoTracking().OrderBy(p => p.Id)
                .ToPagedSliceAsync(1, PaginationDefaults.MaxPageSize + 1, TestContext.Current.CancellationToken));

        Assert.Equal(0, interceptor.Count);
    }

    [Fact]
    public async Task ToPagedSliceAsync_AcceptsTheMaximumPageSize()
    {
        // The bound is inclusive - GetProperties' own tests page at exactly
        // MaxPageSize, so an off-by-one here would reject a legitimate request.
        CommandCountingInterceptor interceptor = new();
        await using CountedContext counted = ContextWith(interceptor);

        (List<Property> page, _) = await counted.Context.Properties.AsNoTracking().OrderBy(p => p.Id)
            .ToPagedSliceAsync(1, PaginationDefaults.MaxPageSize, TestContext.Current.CancellationToken);

        Assert.NotNull(page);
    }

    private async Task SeedPropertiesAsync(string city, int count)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        CatalogDb context = scope.ServiceProvider.GetRequiredService<CatalogDb>();

        for (int i = 0; i < count; i++)
        {
            context.Properties.Add(Property.Create(
                Guid.CreateVersion7(),
                Guid.NewGuid(),
                PropertyType.Hotel,
                LocalizedText.Create(new Dictionary<string, string> { { "en", $"Slice {i}" } }, "en"),
                city,
                CatalogSeeding.TestTimeZoneId));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
