using Bogus;
using BuildingBlocks.Localization;
using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Catalog.Features.CreateUnit;
using Catalog.Features.UpdateUnit;
using Hosts.Features.CreateHost;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Common;

// A localized name is a free-form dictionary on the wire, and LocalizedText.Create rejected only one
// shape of bad input - by throwing ArgumentException, which nothing catches, so the caller got a 500
// for a payload they could fix. Nothing bounded the keys or the lengths at all.
//
// One rule now covers every endpoint that takes one (BuildingBlocks.LocalizedTextRules), so these
// run the same five cases against all six rather than trusting that six validators agree.
[Collection(LocalizationCollection.Name)]
public class LocalizedTextValidationTests(LocalizationFixture factory)
{
    private const string CreateUnit = "create-unit";
    private const string UpdateUnit = "update-unit";
    private const string CreateProperty = "create-property";
    private const string UpdateProperty = "update-property";
    private const string AdminCreateProperty = "admin-create-property";
    private const string CreateHost = "create-host";

    public static TheoryData<string> Endpoints =>
        [CreateUnit, UpdateUnit, CreateProperty, UpdateProperty, AdminCreateProperty, CreateHost];

    private readonly Faker _faker = new Faker();

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithoutTheDefaultCulture_Returns400(string endpoint)
    {
        // The case that used to be a 500: LocalizedText.Create threw ArgumentException here, and
        // GlobalExceptionHandler has no arm for it on purpose.
        await AssertRejectedAsync(endpoint, new Dictionary<string, string> { ["ar"] = "اسم" });
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithAWhitespaceValue_Returns400(string endpoint)
    {
        await AssertRejectedAsync(endpoint, new Dictionary<string, string> { ["en"] = "   " });
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithAnUnsupportedCulture_Returns400(string endpoint)
    {
        // Nothing serves "fr", so a value stored under it can never be read back - it would sit in
        // jsonb forever, invisible.
        await AssertRejectedAsync(endpoint, new Dictionary<string, string> { ["en"] = "Name", ["fr"] = "Nom" });
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithAnOverLengthValue_Returns400(string endpoint)
    {
        await AssertRejectedAsync(
            endpoint,
            new Dictionary<string, string> { ["en"] = new string('a', LocalizedTextRules.MaxNameLength + 1) });
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithEveryValueValid_Succeeds(string endpoint)
    {
        // The other half: the rule has to accept the payload the product is for, in both cultures.
        HttpResponseMessage response = await SendAsync(
            endpoint, new Dictionary<string, string> { ["en"] = "Sea View Suite", ["ar"] = "جناح بإطلالة على البحر" });

        Assert.True(response.IsSuccessStatusCode,
            $"{endpoint} refused a valid payload with {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task AssertRejectedAsync(string endpoint, Dictionary<string, string> localized)
    {
        HttpResponseMessage response = await SendAsync(endpoint, localized);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);

        // camelCased by GlobalExceptionHandler, once, for every ValidationException - so a caller
        // parsing these does not have to know which layer rejected the request.
        string key = endpoint == CreateHost ? "displayName" : "name";
        Assert.Contains(key, problem.Errors.Keys);
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, Dictionary<string, string> localized)
    {
        HttpClient client = factory.CreateClient();

        switch (endpoint)
        {
            case CreateHost:
            {
                string adminToken = await IntegrationTestAdmin.SignInAsync(client, TestContext.Current.CancellationToken);

                return await client.SendAsync(Authorized(HttpMethod.Post, "/api/hosts", adminToken, new CreateHostRequest
                {
                    BusinessName = _faker.Company.CompanyName(),
                    ContactEmail = _faker.Internet.Email(),
                    DisplayName = localized
                }), TestContext.Current.CancellationToken);
            }

            case AdminCreateProperty:
            {
                string adminToken = await IntegrationTestAdmin.SignInAsync(client, TestContext.Current.CancellationToken);
                Guid hostId = await CreateHostAsync(client, adminToken);

                return await client.SendAsync(
                    Authorized(HttpMethod.Post, $"/api/hosts/{hostId}/properties", adminToken, new
                    {
                        HostId = hostId,
                        TimeZoneId = "Asia/Kuwait",
                        PropertyType = PropertyType.Hotel,
                        Name = localized,
                        City = "Kuwait City"
                    }), TestContext.Current.CancellationToken);
            }

            case CreateProperty:
            {
                string hostToken = await HostTokenAsync(client);

                return await client.SendAsync(
                    Authorized(HttpMethod.Post, "/api/catalog/properties", hostToken, new CreatePropertyRequest
                    {
                        TimeZoneId = "Asia/Kuwait",
                        PropertyType = PropertyType.Hotel,
                        Name = localized,
                        City = "Kuwait City"
                    }), TestContext.Current.CancellationToken);
            }

            case UpdateProperty:
            {
                string hostToken = await HostTokenAsync(client);
                Guid propertyId = await CreatePropertyAsync(client, hostToken);

                return await client.SendAsync(
                    Authorized(HttpMethod.Put, $"/api/catalog/properties/{propertyId}", hostToken, new
                    {
                        PropertyId = propertyId,
                        PropertyType = PropertyType.Hotel,
                        Name = localized,
                        City = "Kuwait City",
                        TimeZoneId = "Asia/Kuwait"
                    }), TestContext.Current.CancellationToken);
            }

            case CreateUnit:
            {
                string hostToken = await HostTokenAsync(client);
                Guid propertyId = await CreatePropertyAsync(client, hostToken);

                return await client.SendAsync(
                    Authorized(HttpMethod.Post, "/api/catalog/units", hostToken, new CreateUnitRequest
                    {
                        PropertyId = propertyId,
                        Name = localized,
                        MaxOccupancy = 2,
                        BasePrice = 100m,
                        Currency = Currency.KWD
                    }), TestContext.Current.CancellationToken);
            }

            case UpdateUnit:
            {
                string hostToken = await HostTokenAsync(client);
                Guid propertyId = await CreatePropertyAsync(client, hostToken);
                Guid unitId = await CreateUnitAsync(client, hostToken, propertyId);

                return await client.SendAsync(
                    Authorized(HttpMethod.Put, $"/api/catalog/units/{unitId}", hostToken, new UpdateUnitRequest
                    {
                        UnitId = unitId,
                        Name = localized,
                        MaxOccupancy = 2,
                        BasePrice = 100m,
                        Currency = Currency.KWD,
                        // Required on update: a unit always already has a policy, so the request
                        // carries the current one forward (UpdateUnitRequest).
                        CancellationTiers = [new CancellationTier(0, 0m)]
                    }), TestContext.Current.CancellationToken);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown endpoint.");
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken, object body)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: TestJsonOptions.Default)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<Guid> CreateHostAsync(HttpClient client, string adminToken)
    {
        HttpResponseMessage response = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/hosts", adminToken, new CreateHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email()
            }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateHostResponse? created = await response.Content
            .ReadFromJsonAsync<CreateHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        return created.HostId;
    }

    private async Task<string> HostTokenAsync(HttpClient client)
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = new ApplicationUser { Id = Guid.CreateVersion7(), Email = email, UserName = email };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
        }

        HttpResponseMessage signIn = await client.PostAsJsonAsync("/api/auth/sign-in",
            new SignInRequest { Email = email, Password = password }, TestContext.Current.CancellationToken);
        SignInResponse? signedIn = await signIn.Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signedIn?.AccessToken);

        HttpResponseMessage becameHost = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/hosts/become", signedIn.AccessToken, new BecomeHostRequest
            {
                BusinessName = _faker.Company.CompanyName(),
                ContactEmail = _faker.Internet.Email()
            }), TestContext.Current.CancellationToken);

        BecomeHostResponse? host = await becameHost.Content
            .ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        // The reissued token, not the sign-in one: that is the one carrying host_id (docs/adr/0030).
        Assert.NotNull(host?.AccessToken);
        return host.AccessToken;
    }

    private async Task<Guid> CreatePropertyAsync(HttpClient client, string hostToken)
    {
        HttpResponseMessage response = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/properties", hostToken, new CreatePropertyRequest
            {
                TimeZoneId = "Asia/Kuwait",
                PropertyType = PropertyType.Hotel,
                Name = new Dictionary<string, string> { ["en"] = "Test Property" },
                City = "Kuwait City"
            }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreatePropertyResponse? created = await response.Content
            .ReadFromJsonAsync<CreatePropertyResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        return created.PropertyId;
    }

    private async Task<Guid> CreateUnitAsync(HttpClient client, string hostToken, Guid propertyId)
    {
        HttpResponseMessage response = await client.SendAsync(
            Authorized(HttpMethod.Post, "/api/catalog/units", hostToken, new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { ["en"] = "Test Unit" },
                MaxOccupancy = 2,
                BasePrice = 100m,
                Currency = Currency.KWD
            }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateUnitResponse? created = await response.Content
            .ReadFromJsonAsync<CreateUnitResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        return created.UnitId;
    }
}
