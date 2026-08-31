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

        // Cross-module write (AppHostsDbContext + AppIdentityDbContext, no
        // shared transaction) - see docs/adr/0003. A partially-failed
        // BecomeHost leaves the caller as a fully functional Customer
        // either way, never broken.
        Guid hostId = await hostRegistrar.RegisterHostAsync(
            request.BusinessName,
            request.ContactEmail,
            request.ContactPhone,
            cancellationToken);

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
