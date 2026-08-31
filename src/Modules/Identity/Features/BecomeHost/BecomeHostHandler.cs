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
using Outbox;
namespace Identity.Features.BecomeHost;

public class BecomeHostHandler(
    AppIdentityDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IdentityOutboxDispatcher dispatcher,
    ICurrentUserProvider currentUserProvider,
    IHostRegistrar hostRegistrar,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<BecomeHostRequest, BecomeHostResponse>
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

        // Cross-module write (AppHostsDbContext + AppIdentityDbContext,
        // no shared transaction) - see docs/adr/0003 for why this is a
        // compensating rollback rather than a distributed transaction. A
        // partially-failed BecomeHost leaves the caller as a fully
        // functional Customer either way, never in a broken state.
        Guid hostId = await hostRegistrar.RegisterHostAsync(
            request.BusinessName,
            request.ContactEmail,
            request.ContactPhone,
            cancellationToken);

        user.HostId = hostId;
        IdentityResult updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            // UserStore.UpdateAsync already catches EF's own
            // DbUpdateConcurrencyException internally and surfaces it here
            // as a failed IdentityResult (ErrorDescriber.ConcurrencyFailure)
            // rather than throwing - so the compensating delete below
            // already runs correctly for two concurrent BecomeHost calls on
            // the same user, no extra try/catch needed. What's worth fixing
            // is the error the loser sees: without this branch it would get
            // a generic "concurrency failure" ValidationException even
            // though a plain retry would now correctly hit the
            // AlreadyAHostException above instead.
            //
            // Enqueued via the outbox instead of a direct DeleteAsync call
            // (see docs/adr/0003) - ChangeTracker.Clear() first since
            // UpdateAsync's own failed save can leave `user` tracked with a
            // stale concurrency token, which would otherwise be
            // re-attempted (and likely re-fail) by the SaveChangesAsync
            // below.
            dbContext.ChangeTracker.Clear();
            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(hostId), IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

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
            // UserManager.AddToRoleAsync throws rather than returning a
            // failed IdentityResult when the role itself doesn't exist
            // (e.g. seed data drift) - that's the realistic way this step
            // actually fails, not the "user already in this role" case
            // Succeeded=false alone would catch, so it's normalized into
            // the same shape here to reach the one rollback below either way.
            roleResult = IdentityResult.Failed(new IdentityError { Description = ex.Message });
        }

        if (!roleResult.Succeeded)
        {
            user.HostId = null;
            await userManager.UpdateAsync(user);

            // ChangeTracker.Clear() for the same reason as the branch above,
            // even though this UpdateAsync just succeeded - keeps the two
            // rollback branches uniform rather than reasoning about tracker
            // state separately for each.
            dbContext.ChangeTracker.Clear();
            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(hostId), IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

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
}
