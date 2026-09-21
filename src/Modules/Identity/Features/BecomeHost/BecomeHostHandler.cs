using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using Hosts.Contracts;
using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using System.Data;
namespace Identity.Features.BecomeHost;

public class BecomeHostHandler(
    IdentityDb dbContext,
    UserManager<ApplicationUser> userManager,
    ICurrentUserProvider currentUserProvider,
    IHostRegistrar hostRegistrar,
    IAuthTokenProvider authTokenProvider,
    ITransactionRunner transactionRunner,
    HybridCache cache) : IRequestHandler<BecomeHostRequest, BecomeHostResponse>
{
    public async ValueTask<BecomeHostResponse> Handle(BecomeHostRequest request, CancellationToken cancellationToken)
    {
        // The endpoint requires authentication, so this should never be
        // null in practice - guarded anyway rather than trusting that.
        Guid userId = currentUserProvider.UserId ?? throw new InvalidCredentialsException();

        // Minted before the transaction (docs/adr/0025). A retry after a lost
        // acknowledgement recognises its own committed link by this host id, and
        // answers with the refresh token whose hash that attempt stored.
        Guid hostId = Guid.CreateVersion7();
        IssuedRefreshToken issuedRefreshToken = IssuedRefreshToken.New();

        try
        {
            // The Host, the link, the role and the refresh token commit together (docs/adr/0003), so no
            // failure leaves a Host without its user, or a linked user without the Host role.
            BecomeHostResponse response = await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
                async token =>
                {
                    ApplicationUser user = await userManager.FindByIdAsync(userId.ToString())
                                           ?? throw new InvalidCredentialsException();

                    if (user.HostId == hostId)
                    {
                        // This request's own attempt committed and lost its
                        // acknowledgement. Everything it wrote committed with the link.
                        return await BuildResponseAsync(user, hostId, issuedRefreshToken.Plaintext);
                    }

                    if (user.HostId is not null)
                    {
                        throw new AlreadyAHostException();
                    }

                    await hostRegistrar.RegisterHostAsync(
                        hostId,
                        request.BusinessName,
                        request.ContactEmail,
                        request.ContactPhone,
                        token);

                    user.HostId = hostId;
                    IdentityResult updateResult = await userManager.UpdateAsync(user);
                    if (!updateResult.Succeeded)
                    {
                        // UserStore.UpdateAsync reports DbUpdateConcurrencyException as a
                        // failed IdentityResult: the user row changed under this request.
                        // Answered after the transaction has rolled back, from committed state.
                        if (updateResult.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
                        {
                            throw new AccountChangedDuringRequestException();
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
                        // drift).
                        roleResult = IdentityResult.Failed(new IdentityError { Description = ex.Message });
                    }

                    if (!roleResult.Succeeded)
                    {
                        throw new ValidationException(
                            "Role",
                            string.Join(" ", roleResult.Errors.Select(e => e.Description)));
                    }

                    // The token the caller arrived with says they are not a host, and it stays
                    // valid for its whole lifetime unless the stamp moves (docs/adr/0030). Not an
                    // identity a retry must recognise, despite being minted here: the replay path
                    // above returns before this and reuses the stamp its committed attempt wrote.
                    await userManager.UpdateSecurityStampAsync(user);

                    // Not a rotation of any specific presented refresh token (this
                    // endpoint doesn't take one) - starts a new family, same as
                    // SignIn/SignUp.
                    string refreshToken = await authTokenProvider.GenerateRefreshToken(
                        user.Id, familyId: null, parentTokenId: null, issuedRefreshToken, token);

                    return await BuildResponseAsync(user, hostId, refreshToken);
                },
                cancellationToken);

            // After the commit, not inside it: an eviction while the new stamp is still uncommitted
            // is undone by the next reader, which would cache the old row for another window
            // (docs/adr/0030).
            await SecurityStamps.InvalidateAsync(cache, userId, cancellationToken);

            return response;
        }
        catch (AccountChangedDuringRequestException)
        {
            // Ask rather than infer. ConcurrencyFailure only says the user row
            // changed - another BecomeHost winning is one cause; a concurrent
            // profile or role edit is another, and "already a host" would be false
            // for that.
            bool alreadyLinked = await dbContext.Users.AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.HostId != null, cancellationToken);

            if (alreadyLinked)
            {
                throw new AlreadyAHostException();
            }

            throw new ConflictException(
                "This account was modified while the request was in progress. Please try again.");
        }
    }

    // Reissued immediately - the token the caller arrived with has no host_id
    // claim, and they shouldn't need to sign out/in again to get one.
    private async Task<BecomeHostResponse> BuildResponseAsync(ApplicationUser user, Guid hostId, string refreshToken)
    {
        IList<string> roles = await userManager.GetRolesAsync(user);

        return new BecomeHostResponse
        {
            HostId = hostId,
            AccessToken = authTokenProvider.GenerateJwtToken(user, roles),
            RefreshToken = refreshToken,
            Roles = [.. roles]
        };
    }

    private sealed class AccountChangedDuringRequestException : Exception;
}
