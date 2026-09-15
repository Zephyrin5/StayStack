using Hosts.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Bogus;
using Hosts;
using Hosts.Contracts;
using Identity.Jobs;
using Identity;
using Identity.Entities;
using Identity.Features.BecomeHost;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Hosts;

[Collection("Integration Tests")]
public class BecomeHostTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly Faker _faker = new Faker();

    [Fact]
    public async Task BecomeHost_WhenTheReconcileJobReclaimsTheIntentMidRequest_SaysSoRatherThanBlamingTheRole()
    {
        // A request still in flight past the ten-minute grace period can have
        // its intent claimed by ReconcileOrphanedHostLinkIntentsJob. The staged
        // intent delete then matches zero rows inside AddToRoleAsync's save,
        // and UserStore reports that as a plain ConcurrencyFailure on
        // roleResult - indistinguishable, by code alone, from the role genuinely
        // failing to assign.
        //
        // The compensations are correct either way; this pins the message.
        // Reporting a role-assignment error here would send someone hunting
        // through seed data for what is really a timeout.
        //
        // Driven through a real seam rather than simulated: RegisterHostAsync
        // runs after the intent is opened and before the delete is staged,
        // which is exactly where the job's claim lands.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IHostRegistrar));
                services.Remove(original);
                services.Add(new ServiceDescriptor(
                    typeof(IHostRegistrar),
                    sp => new ReclaimIntentOnRegister(
                        (IHostRegistrar)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                        () => DeleteIntentForAsync(userId)),
                    original.Lifetime));
            }));

        using HttpClient client = host.CreateClient();
        HttpResponseMessage response = await client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        // 409, not the 400 a role validation failure produces.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("recovery window", body);

        // And specifically NOT the two wrong answers: a role problem, or
        // "you are already a host" when they plainly are not.
        Assert.DoesNotContain("already linked to a host", body);

        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        ApplicationUser user = await identity.Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        // Compensation still ran: the caller is left exactly as they started.
        Assert.Null(user.HostId);
    }

    private async Task DeleteIntentForAsync(Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        await identity.PendingHostLinkIntents
            .Where(i => i.UserId == userId)
            .ExecuteDeleteAsync();
    }

    // Stands in for the reconcile job claiming the intent, at the one point in
    // the production call order where that claim actually lands.
    private sealed class ReclaimIntentOnRegister(IHostRegistrar inner, Func<Task> onRegister) : IHostRegistrar
    {
        public async Task RegisterHostAsync(
            Guid hostId, string businessName, string contactEmail, string? contactPhone, CancellationToken cancellationToken)
        {
            await inner.RegisterHostAsync(hostId, businessName, contactEmail, contactPhone, cancellationToken);
            await onRegister();
        }

        public Task DeleteAsync(Guid hostId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(hostId, cancellationToken);
    }

    [Fact]
    public async Task BecomeHost_ConcurrentRequestsFromTheSameUser_ProduceOneHostAndNoServerError()
    {
        // A double-click, or a client retrying a request still in flight.
        // OpenIntentAsync reads-then-inserts against a unique index on UserId,
        // so both attempts can see no intent and both insert. The loser adopts
        // the committed intent rather than failing with a 500.
        //
        // Adopting has a hazard: hostId is the intent's id, so both attempts
        // share one Host, and a losing attempt that deleted "its" Host would
        // delete the one the winner just linked - a user pointing at a missing
        // row, locked out permanently by a double-click. Hence the assertions
        // below check the Host survives, not merely that nothing returned 500.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        // Separate clients so these genuinely overlap rather than queueing on
        // one connection - same reasoning as the other concurrency tests here.
        Task<HttpResponseMessage>[] attempts =
        [
            factory.CreateClient().SendAsync(CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken),
            factory.CreateClient().SendAsync(CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken)
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // The invariant that actually matters: the user is linked, and linked
        // to a Host that exists.
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        ApplicationUser user = await identity.Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        Assert.NotNull(user.HostId);
        Assert.True(await HostExistsAsync(user.HostId!.Value));
    }

    [Fact]
    public async Task BecomeHost_WhenTheProcessDiesAfterRegisteringTheHost_TheReconcileJobDeletesTheOrphan()
    {
        // The window BecomeHost had no cover for at all. RegisterHostAsync
        // commits a Host in Hosts' database before Identity writes anything;
        // the failed-update branches compensate through the outbox, but a hard
        // process death between the two wrote nothing anywhere - no intent, no
        // outbox row, no job - and the orphaned Host was permanent.
        //
        // Simulated the honest way: write the intent and register the Host
        // exactly as the handler does, then simply stop, which is what a
        // process death looks like from the database. Backdated past the grace
        // period so the job treats it as abandoned.
        (Guid userId, _) = await SeedAndSignInUserAsync();
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            identity.PendingHostLinkIntents.Add(new PendingHostLinkIntent
            {
                Id = hostId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow - PendingHostLinkIntent.ReconcileGrace.Add(TimeSpan.FromMinutes(1))
            });
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);

            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Orphaned Co", "orphan@example.com", null, TestContext.Current.CancellationToken);
        }

        Assert.True(await HostExistsAsync(hostId));

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            ReconcileOrphanedHostLinkIntentsJob job = ActivatorUtilities
                .CreateInstance<ReconcileOrphanedHostLinkIntentsJob>(jobScope.ServiceProvider);
            await job.ReconcileAsync(default!, TestContext.Current.CancellationToken);
        }

        Assert.False(await HostExistsAsync(hostId));
        Assert.False(await IntentExistsAsync(hostId));
    }

    [Fact]
    public async Task BecomeHost_WhenTheProcessDiesAfterLinkingButBeforeTheRole_TheJobUnlinksTheUserAndTheyCanRetry()
    {
        // The lockout state: a crash between linking the Host and adding the
        // role leaves a user linked to a real Host, holding no Host role, with
        // AlreadyAHostException firing on every attempt. The intent spans the
        // whole operation, so it is still there to recover from.
        //
        // Seeded as that exact state: intent alive, Host registered, user
        // linked, no role.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            identity.PendingHostLinkIntents.Add(new PendingHostLinkIntent
            {
                Id = hostId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow - PendingHostLinkIntent.ReconcileGrace.Add(TimeSpan.FromMinutes(1))
            });

            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Half Linked Co", "half@example.com", null, TestContext.Current.CancellationToken);

            ApplicationUser user = await identity.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            user.HostId = hostId;
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            ReconcileOrphanedHostLinkIntentsJob job = ActivatorUtilities
                .CreateInstance<ReconcileOrphanedHostLinkIntentsJob>(jobScope.ServiceProvider);
            await job.ReconcileAsync(default!, TestContext.Current.CancellationToken);
        }

        // Both halves undone, not just the Host - unlinking is what makes the
        // difference between recovery and a user pointing at a deleted row.
        Assert.False(await HostExistsAsync(hostId));
        Assert.False(await IntentExistsAsync(hostId));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            ApplicationUser user = await identity.Users.AsNoTracking()
                .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            Assert.Null(user.HostId);
        }

        // The property that actually matters: the user is not stuck. Without
        // recovery this returns 409 AlreadyAHostException forever.
        HttpResponseMessage retry = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Reconcile_WhenTheUserIsLinkedToADifferentHost_LeavesThatLinkAlone()
    {
        // The unlink is guarded on the id. If the user became a host some
        // other way after this intent was abandoned, clearing their HostId
        // would break a perfectly good link to undo an unrelated one.
        (Guid userId, _) = await SeedAndSignInUserAsync();
        Guid abandonedHostId = Guid.CreateVersion7();
        Guid realHostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            identity.PendingHostLinkIntents.Add(new PendingHostLinkIntent
            {
                Id = abandonedHostId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow - PendingHostLinkIntent.ReconcileGrace.Add(TimeSpan.FromMinutes(1))
            });

            IHostRegistrar registrar = scope.ServiceProvider.GetRequiredService<IHostRegistrar>();
            await registrar.RegisterHostAsync(
                abandonedHostId, "Abandoned Co", "abandoned@example.com", null, TestContext.Current.CancellationToken);
            await registrar.RegisterHostAsync(
                realHostId, "Real Co", "real@example.com", null, TestContext.Current.CancellationToken);

            ApplicationUser user = await identity.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            user.HostId = realHostId;
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            ReconcileOrphanedHostLinkIntentsJob job = ActivatorUtilities
                .CreateInstance<ReconcileOrphanedHostLinkIntentsJob>(jobScope.ServiceProvider);
            await job.ReconcileAsync(default!, TestContext.Current.CancellationToken);
        }

        Assert.False(await HostExistsAsync(abandonedHostId));
        Assert.True(await HostExistsAsync(realHostId));

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppIdentityDbContext assertIdentity = assertScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        ApplicationUser linked = await assertIdentity.Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        Assert.Equal(realHostId, linked.HostId);
    }

    [Fact]
    public async Task Reconcile_WhenOneIntentThrows_StillProcessesTheRest()
    {
        // One failing host deletion must not starve the queue. Deleting the Host
        // is an outbox row (docs/adr/0025), committed with the unlink and
        // dispatched after, so a failing registrar cannot fail a reconcile -
        // TryDispatchAsync absorbs it and the relay retries. This asserts that
        // BOTH intents resolve locally, even the one whose host deletion will
        // fail.
        (Guid poisonUserId, _) = await SeedAndSignInUserAsync();
        (Guid healthyUserId, _) = await SeedAndSignInUserAsync();

        Guid poisonHostId = Guid.CreateVersion7();
        Guid healthyHostId = Guid.CreateVersion7();

        await SeedAbandonedIntentAsync(poisonUserId, poisonHostId);
        await SeedAbandonedIntentAsync(healthyUserId, healthyHostId);

        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IHostRegistrar));
                services.Remove(original);
                services.Add(new ServiceDescriptor(
                    typeof(IHostRegistrar),
                    sp => new ThrowForOneHost(
                        (IHostRegistrar)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                        poisonHostId),
                    original.Lifetime));
            }));

        using (IServiceScope jobScope = host.Services.CreateScope())
        {
            ReconcileOrphanedHostLinkIntentsJob job = ActivatorUtilities
                .CreateInstance<ReconcileOrphanedHostLinkIntentsJob>(jobScope.ServiceProvider);

            // The run itself must not throw.
            await job.ReconcileAsync(default!, TestContext.Current.CancellationToken);
        }

        // Both intents are resolved. The local decision - unlink the user,
        // delete the intent - does not depend on a cross-module call
        // succeeding, so one row cannot block the others.
        Assert.False(await IntentExistsAsync(poisonHostId));
        Assert.False(await IntentExistsAsync(healthyHostId));

        // The healthy host is gone, dispatched inline after its commit.
        Assert.False(await HostExistsAsync(healthyHostId));

        // The poisoned one survives its failed dispatch, and that is a
        // durable outbox row's problem rather than a lost write: the relay
        // will keep trying. Nothing about it held up the row behind it.
        Assert.True(await HostExistsAsync(poisonHostId));
    }

    private async Task SeedAbandonedIntentAsync(Guid userId, Guid hostId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        identity.PendingHostLinkIntents.Add(new PendingHostLinkIntent
        {
            Id = hostId,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow - PendingHostLinkIntent.ReconcileGrace.Add(TimeSpan.FromMinutes(1))
        });
        await identity.SaveChangesAsync(TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
            hostId, "Abandoned Co", "abandoned@example.com", null, TestContext.Current.CancellationToken);
    }

    // Fails one host id and passes everything else through, so the batch has a
    // genuine mix rather than an all-or-nothing outcome.
    private sealed class ThrowForOneHost(IHostRegistrar inner, Guid poisonHostId) : IHostRegistrar
    {
        public Task RegisterHostAsync(
            Guid hostId, string businessName, string contactEmail, string? contactPhone, CancellationToken cancellationToken) =>
            inner.RegisterHostAsync(hostId, businessName, contactEmail, contactPhone, cancellationToken);

        public Task DeleteAsync(Guid hostId, CancellationToken cancellationToken) =>
            hostId == poisonHostId
                ? throw new InvalidOperationException("Hosts is unreachable for this row.")
                : inner.DeleteAsync(hostId, cancellationToken);
    }

    [Fact]
    public async Task BecomeHost_WhenTheIntentIsStillInsideTheGracePeriod_TheJobLeavesItAlone()
    {
        // The other half: a slow-but-healthy request must not have its Host
        // deleted out from under it.
        (Guid userId, _) = await SeedAndSignInUserAsync();
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            identity.PendingHostLinkIntents.Add(new PendingHostLinkIntent
            {
                Id = hostId,
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);

            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "In Flight Co", "inflight@example.com", null, TestContext.Current.CancellationToken);
        }

        using (IServiceScope jobScope = factory.Services.CreateScope())
        {
            ReconcileOrphanedHostLinkIntentsJob job = ActivatorUtilities
                .CreateInstance<ReconcileOrphanedHostLinkIntentsJob>(jobScope.ServiceProvider);
            await job.ReconcileAsync(default!, TestContext.Current.CancellationToken);
        }

        Assert.True(await HostExistsAsync(hostId));
        Assert.True(await IntentExistsAsync(hostId));
    }

    [Fact]
    public async Task DeleteHost_ForAnArchivedHost_StillRemovesIt()
    {
        // Both registrar operations ignore the soft-delete filter. If deletion
        // honoured it, an archived Host adopted by registration would be
        // invisible to the compensation meant to undo it: the reconcile job's
        // delete would no-op, and the user would stay linked to a Host nothing
        // could remove.
        //
        // Latent, since nothing archives a Host today; the first feature that
        // does would otherwise discover this by way of an unremovable link.
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Archived Co", "archived@example.com", null, TestContext.Current.CancellationToken);

            AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
            Host host = await hosts.Hosts.SingleAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);
            host.Archive(DateTimeOffset.UtcNow, null);
            await hosts.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>()
                .DeleteAsync(hostId, TestContext.Current.CancellationToken);
        }

        Assert.False(await HostExistsAsync(hostId));
    }

    [Fact]
    public async Task RegisterHost_WhenAnArchivedHostOccupiesTheId_DoesNotCreateASecond()
    {
        // The other half of the symmetry: an archived Host still occupies the
        // primary key. Registration has to see it, or the insert collides and
        // the unique-violation catch adopts it anyway - implicitly, through an
        // exception, instead of by an explicit check.
        Guid hostId = Guid.CreateVersion7();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Archived Co", "archived@example.com", null, TestContext.Current.CancellationToken);

            AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
            Host host = await hosts.Hosts.SingleAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);
            host.Archive(DateTimeOffset.UtcNow, null);
            await hosts.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Archived Co", "archived@example.com", null, TestContext.Current.CancellationToken);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppHostsDbContext assertHosts = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int count = await assertHosts.Hosts
            .IgnoreQueryFilters()
            .CountAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterHost_CalledRepeatedlyWithTheSameId_CreatesExactlyOneHost()
    {
        // The id is supplied by the caller and recorded in the intent first, so
        // every retry after a timeout re-registers the same Host. A registrar
        // minting its own id would let each retry past the "already a host"
        // guard (HostId still null) create another orphan.
        Guid hostId = Guid.CreateVersion7();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IHostRegistrar>().RegisterHostAsync(
                hostId, "Retried Co", "retried@example.com", null, TestContext.Current.CancellationToken);
        }

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppHostsDbContext hosts = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int count = await hosts.Hosts
            .IgnoreQueryFilters()
            .CountAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BecomeHost_WhoseIntentInsertLosesItsAcknowledgement_StillMakesTheUserAHost()
    {
        // The intent is a single-row save, retried by the execution strategy.
        // After a lost acknowledgement the retry re-inserts the same intent and
        // Postgres reports its primary key - so the adopt-the-committed-intent
        // catch has to match the primary key as well as the user index.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        CommitFault<AppIdentityDbContext> lostAck = CommitFaults.FailAfterAutocommit<AppIdentityDbContext>(context =>
            context.ChangeTracker.Entries<PendingHostLinkIntent>().Any(e => e.Entity.UserId == userId));
        using WebApplicationFactory<Program> host = factory.WithCommitFault(lostAck);

        HttpResponseMessage response = await host.CreateClient().SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        Assert.True(lostAck.HasFired, "The lost acknowledgement never reached the intent insert.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        ApplicationUser user = await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        Assert.NotNull(user.HostId);
        Assert.True(await HostExistsAsync(user.HostId!.Value));
    }

    [Fact]
    public async Task BecomeHost_OnSuccess_LeavesNoIntentBehind()
    {
        // The success path deletes the intent in the same SaveChanges that
        // sets HostId, which is what makes it impossible for the reconcile job
        // to delete a live Host. If this ever regressed to a separate delete,
        // a crash in between would leave a linked user with a surviving
        // intent - and the job would collect their real Host.
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        HttpResponseMessage response = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

        Assert.False(await identity.PendingHostLinkIntents
            .AsNoTracking()
            .AnyAsync(i => i.UserId == userId, TestContext.Current.CancellationToken));
    }

    private async Task<bool> HostExistsAsync(Guid hostId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppHostsDbContext hosts = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        return await hosts.Hosts
            .IgnoreQueryFilters()
            .AnyAsync(h => h.Id == hostId, TestContext.Current.CancellationToken);
    }

    private async Task<bool> IntentExistsAsync(Guid intentId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppIdentityDbContext identity = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
        return await identity.PendingHostLinkIntents
            .AsNoTracking()
            .AnyAsync(i => i.Id == intentId, TestContext.Current.CancellationToken);
    }

    private async Task<(Guid UserId, string AccessToken)> SeedAndSignInUserAsync()
    {
        string email = _faker.Internet.Email();
        string password = $"P@1{_faker.Internet.Password()}!";

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email
        };

        IdentityResult createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, "Failed to seed test user.");

        HttpResponseMessage signInResponse = await _client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = email,
            Password = password
        }, TestContext.Current.CancellationToken);

        SignInResponse? signInResult = await signInResponse.Content.ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(signInResult?.AccessToken);

        return (user.Id, signInResult.AccessToken);
    }

    private static BecomeHostRequest CreateValidRequest()
    {
        return new BecomeHostRequest
        {
            BusinessName = "Test Business",
            ContactEmail = "contact@test-business.com"
        };
    }

    private static HttpRequestMessage CreateBecomeHostRequest(string accessToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/hosts/become")
        {
            Content = JsonContent.Create(CreateValidRequest())
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn200_AndLinkHostToUser_WhenRequestIsValid()
    {
        // Arrange
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        // Act
        HttpResponseMessage response = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        BecomeHostResponse? result = await response.Content.ReadFromJsonAsync<BecomeHostResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HostId);
        Assert.Contains("Host", result.Roles);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));

        using IServiceScope scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? persistedUser = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(persistedUser);
        Assert.Equal(result.HostId, persistedUser.HostId);

        AppHostsDbContext hostsDb = scope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        bool hostExists = await hostsDb.Hosts.AnyAsync(h => h.Id == result.HostId, TestContext.Current.CancellationToken);
        Assert.True(hostExists);
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn409_WhenAccountAlreadyHasHost()
    {
        // Arrange: first call succeeds and links a host
        (_, string accessToken) = await SeedAndSignInUserAsync();
        HttpResponseMessage firstResponse = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act: same account tries again
        HttpResponseMessage response = await _client.SendAsync(
            CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task BecomeHost_ShouldReturn401_WhenNotAuthenticated()
    {
        // Act: no Authorization header attached
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/hosts/become", CreateValidRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BecomeHost_ShouldNotLeaveOrphanedHostOrDanglingHostId_WhenRoleAssignmentFails()
    {
        // Arrange
        (Guid userId, string accessToken) = await SeedAndSignInUserAsync();

        using IServiceScope preScope = factory.Services.CreateScope();
        AppHostsDbContext preHostsDb = preScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
        int hostCountBefore = await preHostsDb.Hosts.CountAsync(TestContext.Current.CancellationToken);

        // Force the AddToRoleAsync step inside BecomeHostHandler to fail by
        // removing the "Host" role it depends on, rather than mocking
        // UserManager - this exercises the handler's actual compensating
        // rollback (undo the HostId link, delete the Host record it had
        // just created) against a real database, instead of just trusting
        // the code comment that describes it.
        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppIdentityDbContext identityDb = seedScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            var hostRole = await identityDb.Roles.SingleAsync(
                r => r.Name == "Host", TestContext.Current.CancellationToken);
            identityDb.Roles.Remove(hostRole);
            await identityDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            // Act
            HttpResponseMessage response = await _client.SendAsync(
                CreateBecomeHostRequest(accessToken), TestContext.Current.CancellationToken);

            // Assert: the call must not report success while actually
            // leaving the account without the role it asked for.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using IServiceScope assertScope = factory.Services.CreateScope();
            var userManager = assertScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? persistedUser = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(persistedUser);
            Assert.Null(persistedUser.HostId); // rolled back, not left dangling

            AppHostsDbContext hostsDb = assertScope.ServiceProvider.GetRequiredService<AppHostsDbContext>();
            int hostCountAfter = await hostsDb.Hosts.CountAsync(TestContext.Current.CancellationToken);
            Assert.Equal(hostCountBefore, hostCountAfter); // no orphaned Host row survived
        }
        finally
        {
            // Restore the role so later tests in this shared-container
            // collection aren't affected by this test's setup.
            using IServiceScope cleanupScope = factory.Services.CreateScope();
            AppIdentityDbContext identityDb = cleanupScope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            bool roleStillMissing = !await identityDb.Roles.AnyAsync(
                r => r.Name == "Host", TestContext.Current.CancellationToken);
            if (roleStillMissing)
            {
                identityDb.Roles.Add(new IdentityRole<Guid>
                {
                    Id = Guid.Parse("01a00be7-ddff-7598-bfaa-256e7999a546"),
                    Name = "Host",
                    NormalizedName = "HOST"
                });
                await identityDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }
    }
}
