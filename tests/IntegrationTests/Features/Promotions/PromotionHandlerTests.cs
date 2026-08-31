using Bogus;
using BuildingBlocks.Pagination;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promotions;
using Promotions.Entities;
using Promotions.Enums;
using Promotions.Features;
using Promotions.Features.AdminCreatePromotion;
using Promotions.Features.CreatePromotion;
using Promotions.Features.UpdatePromotion;
using SeedWork.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Promotions;

// End-to-end over real HTTP, same reasoning as PricingRuleHandlerTests -
// ownership enforcement (host vs admin vs platform-wide) is what these
// mostly exist to prove.
[Collection("Integration Tests")]
public class PromotionHandlerTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    private async Task<string> SignInAsSeededAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = "admin@staystack.com",
            Password = "1234"
        }, TestContext.Current.CancellationToken);

        SignInResponse? result = await response.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result?.AccessToken);
        return result.AccessToken;
    }

    private async Task<(Guid HostId, string AccessToken)> SeedHostUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);
        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        using HttpRequestMessage becomeHostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(new BecomeHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email()
            })
        };
        becomeHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signInResult.AccessToken);
        HttpResponseMessage becomeHostResponse = await _client.SendAsync(becomeHostRequest, TestContext.Current.CancellationToken);
        BecomeHostResponse? becomeHostResult =
            await becomeHostResponse.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(becomeHostResult?.AccessToken);

        return (becomeHostResult.HostId, becomeHostResult.AccessToken);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken, object? body = null)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<CreatePromotionResponse> CreateHostPromotionAsync(
        string hostAccessToken, string? code = null, PromotionDiscountType discountType = PromotionDiscountType.Percentage,
        decimal discountValue = 10m, Currency? currency = null)
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions", hostAccessToken, new CreatePromotionRequest
            {
                Code = code ?? _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = discountType,
                DiscountValue = discountValue,
                Currency = currency
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePromotionResponse? result = await response.Content.ReadFromJsonAsync<CreatePromotionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public async Task Create_ShouldReturn200_AndPersistPromotion_ForHost()
    {
        (Guid hostId, string hostToken) = await SeedHostUserAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();

        CreatePromotionResponse created = await CreateHostPromotionAsync(hostToken, code, PromotionDiscountType.Percentage, 15m);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == created.PromotionId, TestContext.Current.CancellationToken);
        Assert.Equal(code, promotion.Code);
        Assert.Equal(hostId, promotion.HostId);
        Assert.Equal(15m, promotion.DiscountValue);
        Assert.Equal(0, promotion.RedemptionCount);
    }

    [Fact]
    public async Task Create_ShouldNormalizeCode_ToUppercaseAndTrimmed()
    {
        (_, string hostToken) = await SeedHostUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions", hostToken, new CreatePromotionRequest
            {
                Code = "  summer26  ",
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePromotionResponse? created = await response.Content.ReadFromJsonAsync<CreatePromotionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == created.PromotionId, TestContext.Current.CancellationToken);
        Assert.Equal("SUMMER26", promotion.Code);
    }

    [Fact]
    public async Task Create_ShouldReturn400_WhenCodeAlreadyInUse()
    {
        (_, string hostToken) = await SeedHostUserAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        await CreateHostPromotionAsync(hostToken, code);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions", hostToken, new CreatePromotionRequest
            {
                Code = code,
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldReturn400_WhenFixedAmountHasNoCurrency()
    {
        (_, string hostToken) = await SeedHostUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions", hostToken, new CreatePromotionRequest
            {
                Code = _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = PromotionDiscountType.FixedAmount,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminCreate_ShouldAllowNullHostId_ForPlatformWidePromotion()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions/admin", adminToken, new AdminCreatePromotionRequest
            {
                HostId = null,
                Code = _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePromotionResponse? created = await response.Content.ReadFromJsonAsync<CreatePromotionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == created.PromotionId, TestContext.Current.CancellationToken);
        Assert.Null(promotion.HostId);
    }

    [Fact]
    public async Task AdminCreate_ShouldReturn404_WhenHostIdDoesNotExist()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions/admin", adminToken, new AdminCreatePromotionRequest
            {
                HostId = Guid.NewGuid(),
                Code = _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_ShouldReturn200_AndPersistChanges_ForOwningHost()
    {
        (_, string hostToken) = await SeedHostUserAsync();
        CreatePromotionResponse created = await CreateHostPromotionAsync(hostToken, discountValue: 10m);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/promotions/{created.PromotionId}", hostToken, new UpdatePromotionRequest
            {
                PromotionId = created.PromotionId,
                DiscountValue = 25m,
                MaxRedemptions = 50
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == created.PromotionId, TestContext.Current.CancellationToken);
        Assert.Equal(25m, promotion.DiscountValue);
        Assert.Equal(50, promotion.MaxRedemptions);
    }

    [Fact]
    public async Task Update_ShouldReturn404_ForNonOwningHost()
    {
        (_, string ownerToken) = await SeedHostUserAsync();
        (_, string otherHostToken) = await SeedHostUserAsync();
        CreatePromotionResponse created = await CreateHostPromotionAsync(ownerToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/promotions/{created.PromotionId}", otherHostToken, new UpdatePromotionRequest
            {
                PromotionId = created.PromotionId,
                DiscountValue = 99m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_ShouldReturn404_ForHostTargetingPlatformWidePromotion()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (_, string hostToken) = await SeedHostUserAsync();

        HttpResponseMessage createResponse = await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions/admin", adminToken, new AdminCreatePromotionRequest
            {
                HostId = null,
                Code = _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);
        CreatePromotionResponse? created = await createResponse.Content.ReadFromJsonAsync<CreatePromotionResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Put, $"/api/catalog/promotions/{created.PromotionId}", hostToken, new UpdatePromotionRequest
            {
                PromotionId = created.PromotionId,
                DiscountValue = 99m
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ShouldArchivePromotion_AndExcludeItFromMineList()
    {
        (_, string hostToken) = await SeedHostUserAsync();
        CreatePromotionResponse created = await CreateHostPromotionAsync(hostToken);

        HttpResponseMessage deleteResponse = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/promotions/{created.PromotionId}", hostToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppPromotionsDbContext db = scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>();
        Promotion archived = await db.Promotions.IgnoreQueryFilters()
            .SingleAsync(p => p.Id == created.PromotionId, TestContext.Current.CancellationToken);
        Assert.Equal(EntityStatus.Archived, archived.Status);

        HttpResponseMessage listResponse = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/catalog/promotions/mine", hostToken),
            TestContext.Current.CancellationToken);
        PagedResponse<PromotionSummary>? listResult =
            await listResponse.Content.ReadFromJsonAsync<PagedResponse<PromotionSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(listResult);
        Assert.Empty(listResult.Items);
    }

    [Fact]
    public async Task Create_ShouldSucceed_ReusingACode_FromAnArchivedPromotion()
    {
        // ix_promotions_code is partial (status <> 2 / EntityStatus.Archived,
        // see PromotionConfiguration's own comment) precisely so this works -
        // an unfiltered unique index would have this second Create keep
        // hitting UniqueViolation forever, since "deletion" here is
        // Archive(), not a real row removal.
        (_, string hostToken) = await SeedHostUserAsync();
        string code = _faker.Random.AlphaNumeric(10).ToUpperInvariant();
        CreatePromotionResponse original = await CreateHostPromotionAsync(hostToken, code);

        HttpResponseMessage deleteResponse = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/promotions/{original.PromotionId}", hostToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        CreatePromotionResponse recreated = await CreateHostPromotionAsync(hostToken, code);
        Assert.NotEqual(original.PromotionId, recreated.PromotionId);
    }

    [Fact]
    public async Task Delete_ShouldReturn404_ForNonOwningHost()
    {
        (_, string ownerToken) = await SeedHostUserAsync();
        (_, string otherHostToken) = await SeedHostUserAsync();
        CreatePromotionResponse created = await CreateHostPromotionAsync(ownerToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Delete, $"/api/catalog/promotions/{created.PromotionId}", otherHostToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListMine_ShouldReturnOnlyThisHostsOwnPromotions()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (_, string hostToken) = await SeedHostUserAsync();
        (_, string otherHostToken) = await SeedHostUserAsync();
        CreatePromotionResponse mine = await CreateHostPromotionAsync(hostToken);
        await CreateHostPromotionAsync(otherHostToken);
        await _client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/promotions/admin", adminToken, new AdminCreatePromotionRequest
            {
                HostId = null,
                Code = _faker.Random.AlphaNumeric(10).ToUpperInvariant(),
                DiscountType = PromotionDiscountType.Percentage,
                DiscountValue = 10m
            }),
            TestContext.Current.CancellationToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/catalog/promotions/mine", hostToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PromotionSummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<PromotionSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PromotionSummary only = Assert.Single(result.Items);
        Assert.Equal(mine.PromotionId, only.Id);
    }

    [Fact]
    public async Task GetHostPromotions_ShouldReturnThatHostsPromotions_ForAdmin()
    {
        string adminToken = await SignInAsSeededAdminAsync();
        (Guid hostId, string hostToken) = await SeedHostUserAsync();
        CreatePromotionResponse created = await CreateHostPromotionAsync(hostToken);

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/hosts/{hostId}/promotions", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PagedResponse<PromotionSummary>? result =
            await response.Content.ReadFromJsonAsync<PagedResponse<PromotionSummary>>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        PromotionSummary only = Assert.Single(result.Items);
        Assert.Equal(created.PromotionId, only.Id);
    }

    [Fact]
    public async Task GetHostPromotions_ShouldReturn404_ForNonExistentHost()
    {
        string adminToken = await SignInAsSeededAdminAsync();

        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, $"/api/hosts/{Guid.NewGuid()}/promotions", adminToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
