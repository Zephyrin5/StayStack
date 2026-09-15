using BuildingBlocks.Exceptions;
using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Identity.Features.SignUp;

public class SignUpHandler(
    AppIdentityDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<SignUpRequest, SignUpResponse>
{
    // Matches the literal Name seeded in RoleConfiguration - every
    // self-registered account starts here. Becoming a Host is a separate,
    // later action on the same account, not a registration-time choice -
    // see docs/adr/0005.
    private const string CustomerRoleName = "Customer";

    public async ValueTask<SignUpResponse> Handle(SignUpRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? existingUser = await userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
        {
            throw new EmailAlreadyInUseException();
        }

        ApplicationUser user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        // One transaction. All three writes - the user, the role assignment, the
        // refresh token - land in AppIdentityDbContext on the connection this
        // transaction owns, so a failure anywhere rolls back all of them.
        // BecomeHostHandler also writes Hosts, so it runs in an atomic scope
        // (docs/adr/0003); nothing here does.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        // Chosen once, outside the retry, like the account's own id above
        // (docs/adr/0025). Minted inside RegisterAsync, a retry after a lost
        // acknowledgement would hand back a refresh token no row matches.
        IssuedRefreshToken firstRefreshToken = IssuedRefreshToken.New();

        SignUpResponse response = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // An earlier attempt may already have committed the account, its
            // role and its refresh token together. Asked before CreateAsync,
            // which would otherwise insert the same account id again and fail on
            // the primary key - a 500 for someone who had just registered.
            if (await dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == user.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);

                IList<string> committedRoles = await userManager.GetRolesAsync(user);

                return new SignUpResponse
                {
                    Id = user.Id,
                    UserName = user.UserName,
                    Email = user.Email,
                    AccessToken = authTokenProvider.GenerateJwtToken(user, committedRoles),
                    RefreshToken = firstRefreshToken.Plaintext,
                    Roles = [.. committedRoles]
                };
            }

            SignUpResponse created = await RegisterAsync(user, request, firstRefreshToken, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return created;
        });

        return response;
    }

    /// <summary>
    ///     Everything registration writes, inside the caller's transaction.
    ///     <para>
    ///         UserManager saves through this same scoped
    ///         AppIdentityDbContext, so its own SaveChanges calls enlist in
    ///         that transaction rather than committing beside it.
    ///     </para>
    /// </summary>
    private async Task<SignUpResponse> RegisterAsync(
        ApplicationUser user, SignUpRequest request, IssuedRefreshToken firstRefreshToken,
        CancellationToken cancellationToken)
    {
        IdentityResult createResult = await userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            // Identity's own password-policy/format errors are already
            // safe, specific, user-facing messages (unlike sign-in, where
            // vague-on-purpose is the right call) - surface them directly
            // rather than wrapping in a generic message.
            throw new ValidationException(
                nameof(request.Password),
                string.Join(" ", createResult.Errors.Select(e => e.Description)));
        }

        IdentityResult roleResult;
        try
        {
            roleResult = await userManager.AddToRoleAsync(user, CustomerRoleName);
        }
        catch (InvalidOperationException ex)
        {
            // Same as BecomeHostHandler's identical try/catch: AddToRoleAsync
            // throws, rather than returning a failed IdentityResult, when
            // the role doesn't exist (seed data drift) - normalized here to
            // reach the ValidationException below instead of an unhandled 500.
            roleResult = IdentityResult.Failed(new IdentityError { Description = ex.Message });
        }

        if (!roleResult.Succeeded)
        {
            // Extremely unlikely with seed data in place, but if the Customer
            // role is ever missing, fail loudly rather than leave a roleless
            // account behind. The rollback is what removes the user now - no
            // compensating delete whose own result could be ignored.
            throw new ValidationException(
                "Role",
                string.Join(" ", roleResult.Errors.Select(e => e.Description)));
        }

        var roles = await userManager.GetRolesAsync(user);

        string accessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, familyId: null, parentTokenId: null, firstRefreshToken, cancellationToken);

        return new SignUpResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            Roles = [.. roles]
        };
    }
}
