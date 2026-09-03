using System.Net;
namespace IntegrationTests.Features.Catalog;

// Page had only a lower bound, and the offset is computed as int * int, which
// is unchecked in C#. So a query string could both overflow the offset into a
// negative Skip and, below that threshold, ask Postgres to scan and discard
// billions of rows.
//
// GetProperties is the sharpest case: anonymous, filtered by a subquery over
// candidate unit ids, and cached under a key that includes Page - so each
// distinct page value is both expensive work and a fresh cache entry.
[Collection("Integration Tests")]
public class PaginationBoundsTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetProperties_WithAPageThatOverflowsTheOffset_IsRejected()
    {
        // (30000000 - 1) * 100 = 2,999,999,900, past int.MaxValue, wrapping to
        // about -1.29e9. Postgres refuses a negative OFFSET, so this was a 500
        // reachable from a query string with no auth.
        HttpResponseMessage response = await _client.GetAsync(
            "/api/catalog/properties?page=30000000&pageSize=100", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetProperties_WithAHugeButNonOverflowingPage_IsAlsoRejected()
    {
        // No overflow here - the offset is 2,000,000,000, which Postgres will
        // happily plan, scan and throw away, with CountAsync running the full
        // filter beside it. Capping PageSize bounds the response; only capping
        // the offset bounds the work.
        HttpResponseMessage response = await _client.GetAsync(
            "/api/catalog/properties?page=20000000&pageSize=100", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetProperties_WithAnOrdinaryPage_StillWorks()
    {
        // The bound must not reject real paging. A test that only proves
        // rejection would pass against a cap of zero.
        HttpResponseMessage response = await _client.GetAsync(
            "/api/catalog/properties?page=2&pageSize=20", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
