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
using Npgsql;
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
            // Deliberately no host deletion and no intent discard here, which
            // this branch used to do both of.
            //
            // The hostId is the intent's id, and concurrent attempts for one
            // user now adopt the same intent (see OpenIntentAsync) - so it is
            // the same Host the winning attempt may have just linked itself
            // to. Deleting it would leave that user pointing at a row that no
            // longer exists, with no Host role and AlreadyAHostException
            // firing forever: the permanent lockout, reachable by a
            // double-click. Discarding the intent would then remove the only
            // marker that could recover it.
            //
            // Leaving both in place is safe in every case. If a concurrent
            // attempt won, it deletes the intent itself when it completes and
            // there is nothing to clean up. If nobody completed, the intent
            // outlives the grace period and the reconcile job unlinks and
            // deletes together. The cost is that an abandoned Host lingers for
            // the grace period rather than going immediately, which is a much
            // smaller price than deleting a live one.
            if (updateResult.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            {
                // Ask rather than infer. ConcurrencyFailure only says the user
                // row changed under this request - it does not say who changed
                // it or why. Another BecomeHost winning is one cause; a
                // concurrent profile or role edit is another, and telling
                // someone they are "already a host" because they happened to
                // rename themselves mid-request is simply false.
                bool alreadyLinked = await dbContext.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == userId && u.HostId != null, cancellationToken);

                if (alreadyLinked)
                {
                    throw new AlreadyAHostException();
                }

                throw new ConflictException(
                    "This account was modified while the request was in progress. Please try again.");
            }

            throw new ValidationException(
                "Host",
                string.Join(" ", updateResult.Errors.Select(e => e.Description)));
        }

        // Staged here rather than before the HostId write, so the intent spans
        // the WHOLE cross-module operation instead of only its first half.
        // UserManager.AddToRoleAsync calls through to UpdateUserAsync on this
        // same scoped context, so this delete flushes in that save and the
        // marker disappears exactly when the operation completes.
        //
        // It used to be staged before the HostId update, which made it atomic
        // with the link - correct for the first half, and precisely wrong for
        // the second: the durable marker vanished at the moment the next
        // cross-module inconsistency became possible. A crash between the link
        // committing and the role being added left a user linked to a real
        // Host with no Host role, no marker, and AlreadyAHostException firing
        // on every retry. There was no path back - not in this handler, the
        // outbox, or the job.
        //
        // Nothing flushes this on a failure path: AddToRoleAsync throwing (a
        // missing role) never reaches its save, and a failed IdentityResult
        // means the same. The branch below un-stages it explicitly rather than
        // depending on that.
        dbContext.PendingHostLinkIntents.Remove(intent);

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
            // Did the ROLE fail, or did our own intent delete? AddToRoleAsync's
            // save carries that staged delete, so if ReconcileOrphanedHostLink-
            // IntentsJob claimed this intent while the request was still in
            // flight past the grace period, the delete matches zero rows and
            // UserStore reports it as a plain ConcurrencyFailure on roleResult -
            // indistinguishable from a genuine role failure by the code alone.
            //
            // Read before the Clear below, while `intent` is still usable.
            bool intentReclaimed = !await dbContext.PendingHostLinkIntents.AsNoTracking()
                .AnyAsync(i => i.Id == intent.Id, cancellationToken);

            // Compensate against FRESH state, not the tracked graph this
            // request has been mutating. AddToRoleAsync's failed save left
            // `user` holding a concurrency stamp the database never accepted
            // and a role entry that was rolled back, so re-using it made the
            // unlink below fail too - which then took the "leave it for the
            // job" path, at the one moment the job has nothing left to act on.
            // Clearing also drops the staged intent delete, which must not ride
            // along on the compensating save.
            dbContext.ChangeTracker.Clear();

            ApplicationUser? current = await userManager.FindByIdAsync(userId.ToString());

            // Only unlink what this attempt linked. If the row already points
            // somewhere else - the reconcile job cleared it, or another attempt
            // linked a different host - it is not ours to touch.
            IdentityResult unlinkResult = IdentityResult.Success;
            if (current is not null && current.HostId == hostId)
            {
                current.HostId = null;
                unlinkResult = await userManager.UpdateAsync(current);
            }

            if (!unlinkResult.Succeeded)
            {
                // This result used to be discarded, and that was a permanent
                // lockout. If unlinking fails the user still points at this
                // Host, so deleting it anyway leaves them referencing a row
                // that does not exist, with no Host role and
                // AlreadyAHostException firing forever - strictly worse than
                // leaving both in place.
                //
                // So: no host deletion, and the intent stays. The reconcile
                // job owns it from here, unlinking and deleting together, and
                // retrying every run until both land.
                if (intentReclaimed)
                {
                    throw new ConflictException(
                        "This request took longer than the recovery window allows and was rolled back. Please try again.");
                }

                throw new ValidationException(
                    "Role",
                    string.Join(" ", roleResult.Errors.Select(e => e.Description)));
            }

            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(hostId), IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

            // Only now: the user is unlinked and the deletion is durable, so
            // nothing is left for the job to reconcile.
            await DiscardIntentAsync();

            if (intentReclaimed)
            {
                throw new ConflictException(
                    "This request took longer than the recovery window allows and was rolled back. Please try again.");
            }

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
        string refreshToken = await authTokenProvider.GenerateRefreshToken(user.Id, familyId: null, parentTokenId: null, IssuedRefreshToken.New(), cancellationToken);

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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return intent;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another attempt for this user inserted between the read above
            // and this save - a double-click, or a client retrying a request
            // still in flight. Unhandled this surfaced as a bare
            // DbUpdateException, which GlobalExceptionHandler has no arm for,
            // so two clicks produced a 500. Same shape ConfirmBookingHandler
            // already handles for its own intent.
            //
            // Adopted rather than rejected, because the semantics differ from
            // Bookings': a hold can only be consumed once, so a second
            // claimant there is a genuine conflict. Here both attempts want
            // the same outcome for the same user, so the loser joins the
            // winner's operation - RegisterHostAsync is idempotent under the
            // adopted id, so this produces one Host rather than two.
            //
            // Detached first, or the failed insert stays Added and the next
            // SaveChanges on this context retries it.
            dbContext.Entry(intent).State = EntityState.Detached;

            PendingHostLinkIntent? winners = await dbContext.PendingHostLinkIntents
                .SingleOrDefaultAsync(i => i.UserId == userId, cancellationToken);

            if (winners is not null)
            {
                return winners;
            }

            // SingleOrDefault, not Single. The first version of this assumed
            // the row had to be there - a unique violation means the other
            // transaction committed - and that reasoning missed that the
            // intent is deliberately short-lived: the winning attempt can
            // register, link, add the role and delete its own intent inside
            // the window between our insert failing and this read. It does,
            // routinely, and Single threw "Sequence contains no elements"
            // straight back out as the 500 this was meant to remove.
            //
            // So ask what actually happened rather than assuming. If the
            // winner completed, this user is a host now and that is the
            // honest answer.
            bool alreadyLinked = await dbContext.Users.AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.HostId != null, cancellationToken);

            if (alreadyLinked)
            {
                throw new AlreadyAHostException();
            }

            // Intent gone and the user still unlinked: the other attempt
            // failed and compensated inside the same window. Nothing is wrong
            // and nothing is owed - the caller just lost a race against a
            // request that undid itself, and retrying will now succeed.
            throw new ConflictException(
                "Another attempt to become a host was in progress and did not complete. Please try again.");
        }
    }
}
