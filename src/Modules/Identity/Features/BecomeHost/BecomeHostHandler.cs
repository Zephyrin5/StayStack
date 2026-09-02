using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Hosts.Contracts;
using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Identity.Outbox;
using Identity.Serialization;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Outbox;
namespace Identity.Features.BecomeHost;

public class BecomeHostHandler(
    AppIdentityDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IdentityOutboxDispatcher dispatcher,
    ICurrentUserProvider currentUserProvider,
    IHostRegistrar hostRegistrar,
    IAuthTokenProvider authTokenProvider,
    TimeProvider timeProvider) : IRequestHandler<BecomeHostRequest, BecomeHostResponse>
{
    public async ValueTask<BecomeHostResponse> Handle(BecomeHostRequest request, CancellationToken cancellationToken)
    {
        // The endpoint requires authentication, so this should never be
        // null in practice - guarded anyway rather than trusting that.
        Guid userId = currentUserProvider.UserId ?? throw new InvalidCredentialsException();

        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new InvalidCredentialsException();
        }

        if (user.HostId is not null)
        {
            throw new AlreadyAHostException();
        }

        // Durable intent BEFORE the cross-module call, so a hard process
        // death after RegisterHostAsync commits still leaves something for
        // ReconcileOrphanedHostLinkIntentsJob to find. This was the last
        // forward-half cross-module write in the codebase with no such
        // marker - the failed-update paths below compensate through the
        // outbox, but a crash between the two wrote nothing anywhere and the
        // orphaned Host was permanent. See docs/adr/0017.
        PendingHostLinkIntent intent = await OpenIntentAsync(userId, cancellationToken);

        // ExecuteDelete, not a tracked Remove, on the failure paths: a
        // zero-row delete just means the reconcile job got here first, which
        // has to be a clean no-op. Same shape and same reasoning as
        // ConfirmBookingHandler.DiscardIntentAsync.
        async Task DiscardIntentAsync()
        {
            await dbContext.PendingHostLinkIntents
                .Where(i => i.Id == intent.Id)
                .ExecuteDeleteAsync(cancellationToken);
            dbContext.Entry(intent).State = EntityState.Detached;
        }

        // Cross-module write (AppHostsDbContext + AppIdentityDbContext, no
        // shared transaction) - see docs/adr/0003. A partially-failed
        // BecomeHost leaves the caller as a fully functional Customer
        // either way, never broken.
        //
        // Idempotent under the intent's id: a client retrying after a timeout
        // reuses the same intent (see OpenIntentAsync) and this re-registers
        // the same Host rather than minting another.
        await hostRegistrar.RegisterHostAsync(
            intent.Id,
            request.BusinessName,
            request.ContactEmail,
            request.ContactPhone,
            cancellationToken);

        Guid hostId = intent.Id;

        // Marked for deletion BEFORE UpdateAsync, deliberately: UserManager
        // resolves this same scoped AppIdentityDbContext, so its SaveChanges
        // carries this delete with it and the two commit atomically. That is
        // the structural guarantee the reconcile job depends on - a user whose
        // HostId is set can never have a surviving intent, so the job can
        // never delete a live Host. Resolving the intent separately, after the
        // update, would open exactly that window.
        //
        // If UpdateAsync fails, the delete rolls back with it and the intent
        // survives, which is what the compensating branch below needs.
        dbContext.PendingHostLinkIntents.Remove(intent);

        user.HostId = hostId;
        IdentityResult updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            // UserStore.UpdateAsync already catches EF's DbUpdateConcurrencyException
            // and surfaces it as a failed IdentityResult (ConcurrencyFailure)
            // rather than throwing - the compensating delete below already
            // handles two concurrent BecomeHost calls correctly. Worth
            // fixing is the error the loser sees: without this branch, a
            // plain retry that would now correctly hit AlreadyAHostException
            // above gets a generic concurrency-failure message instead.
            //
            // Enqueued via the outbox (docs/adr/0003) rather than a direct
            // DeleteAsync - ChangeTracker.Clear() first since UpdateAsync's
            // failed save leaves `user` tracked with a stale concurrency
            // token that would otherwise be re-attempted by the
            // SaveChangesAsync below.
            dbContext.ChangeTracker.Clear();
            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(hostId), IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

            // After the compensating save, deliberately - a crash between the
            // two leaves the intent alive and the job repeats the (idempotent)
            // delete, which is safe. Discarding first would drop the marker
            // before the compensation was durable.
            await DiscardIntentAsync();

            if (updateResult.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            {
                throw new AlreadyAHostException();
            }

            throw new ValidationException(
                "Host",
                string.Join(" ", updateResult.Errors.Select(e => e.Description)));
        }

        IdentityResult roleResult;
        try
        {
            roleResult = await userManager.AddToRoleAsync(user, AuthorizationPolicies.Host);
        }
        catch (InvalidOperationException ex)
        {
            // AddToRoleAsync throws, rather than returning a failed
            // IdentityResult, when the role doesn't exist (e.g. seed data
            // drift) - the realistic failure mode here, not "already in
            // this role". Normalized into the same shape to reach the one
            // rollback below either way.
            roleResult = IdentityResult.Failed(new IdentityError { Description = ex.Message });
        }

        if (!roleResult.Succeeded)
        {
            user.HostId = null;
            await userManager.UpdateAsync(user);

            // Same ChangeTracker.Clear() as the branch above, even though
            // this UpdateAsync succeeded - keeps both rollback branches
            // uniform.
            dbContext.ChangeTracker.Clear();
            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(hostId), IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

            // No DiscardIntentAsync here: the intent already committed away
            // with the successful UpdateAsync above, so there is nothing left
            // to delete.

            throw new ValidationException(
                "Role",
                string.Join(" ", roleResult.Errors.Select(e => e.Description)));
        }

        // Reissue tokens immediately - the token the caller arrived with
        // has no host_id claim, and they shouldn't need to sign out/in
        // again just to get one that reflects what they just did.
        var roles = await userManager.GetRolesAsync(user);
        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        // Not a rotation of any specific presented refresh token (this
        // endpoint doesn't take one) - starts a new family, same as
        // SignIn/SignUp.
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, familyId: null, parentTokenId: null, cancellationToken);

        return new BecomeHostResponse
        {
            HostId = hostId,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Roles = [.. roles]
        };
    }

    /// <summary>
    ///     Returns this user's in-flight intent, reusing an existing one
    ///     rather than allocating a second host id.
    ///     <para>
    ///         Reuse is the point. RegisterHostAsync used to generate the id,
    ///         so a client retrying after a timeout looked exactly like a
    ///         first attempt - the "already a host" guard still saw a null
    ///         HostId, and each retry left another orphaned Host. Reusing the
    ///         recorded id makes every retry re-register the same one.
    ///     </para>
    ///     <para>
    ///         The unique index on UserId is the backstop for two attempts
    ///         racing past this lookup: the second insert fails before any
    ///         cross-module call happens, so the orphan count is bounded at
    ///         one either way.
    ///     </para>
    /// </summary>
    private async Task<PendingHostLinkIntent> OpenIntentAsync(Guid userId, CancellationToken cancellationToken)
    {
        PendingHostLinkIntent? existing = await dbContext.PendingHostLinkIntents
            .SingleOrDefaultAsync(i => i.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        PendingHostLinkIntent intent = new PendingHostLinkIntent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = timeProvider.GetUtcNow()
        };

        dbContext.PendingHostLinkIntents.Add(intent);
        await dbContext.SaveChangesAsync(cancellationToken);

        return intent;
    }
}
