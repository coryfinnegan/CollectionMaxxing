using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Invites;

/// <summary>Use case: the owner invites someone as an editor or a viewer (docs/04, <c>POST …/invites</c>).</summary>
public static class CreateInvite
{
    public sealed record Command(Guid LibraryId, string? Name, LibraryRole? Role);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IIdGenerator ids, TimeProvider time, IPublicServerUrl server)
    {
        public async Task<Result<CreatedInviteDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var name = command.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                return InviteErrors.NameRequired;
            }

            if (name.Length > LibraryInvite.MaxNameLength)
            {
                return InviteErrors.NameTooLong;
            }

            if (command.Role is not { } role)
            {
                return InviteErrors.RoleRequired;
            }

            // An invite can never make an owner, whatever the request says.
            if (role is not (LibraryRole.Editor or LibraryRole.Viewer))
            {
                return InviteErrors.RoleNotInvitable;
            }

            if (currentUser.UserId is not { } userId || currentUser.RoleIn(command.LibraryId) != LibraryRole.Owner)
            {
                return Errors.LibraryNotFound;
            }

            var now = time.GetUtcNow();
            var token = InviteSecrets.NewToken();
            var invite = new LibraryInvite
            {
                Id = ids.NewId(),
                LibraryId = command.LibraryId,
                Role = role,
                Name = name,
                TokenHash = InviteSecrets.Hash(token),
                CreatedBy = userId,
                CreatedAt = now,
                ExpiresAt = now + InviteSecrets.Lifetime,
            };
            db.LibraryInvites.Add(invite);
            await db.SaveChangesAsync(cancellationToken);

            var link = $"{server.Value}/join/{token}";
            return new CreatedInviteDto(ToDto(invite), link, link);
        }
    }

    internal static InviteDto ToDto(LibraryInvite invite) => new(
        invite.Id, invite.Name, invite.Role, invite.CreatedAt, invite.ExpiresAt, invite.AcceptedBy, invite.AcceptedAt, invite.RevokedAt);
}

/// <summary>Use case: the invites of a library, newest first: waiting, used and revoked.</summary>
public static class ListInvites
{
    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Page<InviteDto>> HandleAsync(Guid libraryId, CancellationToken cancellationToken)
        {
            var invites = await db.LibraryInvites.AsNoTracking()
                .Where(i => i.LibraryId == libraryId)
                .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
                .ToListAsync(cancellationToken);
            return new Page<InviteDto>([.. invites.Select(CreateInvite.ToDto)]);
        }
    }
}

/// <summary>Use case: the owner revokes an invite; its link stops working at once.</summary>
public static class RevokeInvite
{
    public sealed class Handler(IAppDbContext db, TimeProvider time)
    {
        /// <summary>An invite of another library is the same "not found" as one that doesn't exist. Revoking twice is fine; a used invite can't be revoked.</summary>
        public async Task<Result> HandleAsync(Guid libraryId, Guid inviteId, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            var revoked = await db.LibraryInvites
                .Where(i => i.Id == inviteId && i.LibraryId == libraryId && i.RevokedAt == null && i.AcceptedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.RevokedAt, now), cancellationToken);
            if (revoked == 1)
            {
                return Result.Success();
            }

            var invite = await db.LibraryInvites.AsNoTracking()
                .Where(i => i.Id == inviteId && i.LibraryId == libraryId)
                .Select(i => new { i.AcceptedAt })
                .FirstOrDefaultAsync(cancellationToken);
            return invite is null
                ? Result.Failure(InviteErrors.NotFound)
                : invite.AcceptedAt is not null ? Result.Failure(InviteErrors.AlreadyAccepted) : Result.Success();
        }
    }
}

/// <summary>Use case: what the join page shows. Anyone with the link may ask; nobody learns anything about a token that isn't usable.</summary>
public static class GetInvitePreview
{
    public sealed class Handler(IAppDbContext db, IUserDirectory users, TimeProvider time)
    {
        public async Task<Result<InvitePreviewDto>> HandleAsync(string? token, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            var hash = InviteSecrets.Hash(token ?? string.Empty);

            // An unknown, an expired, a used and a revoked token must cost the same and answer the same: every token runs the same
            // lookups, and only the last line decides. (The filters are off because nobody is signed in.)
            var invite = await db.LibraryInvites.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
            var libraryId = invite?.LibraryId ?? Guid.Empty;
            var libraryName = await db.Libraries.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.Id == libraryId).Select(l => l.Name).FirstOrDefaultAsync(cancellationToken);
            var ownerId = await db.LibraryMembers.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.LibraryId == libraryId && m.Role == LibraryRole.Owner).Select(m => m.UserId).FirstOrDefaultAsync(cancellationToken);
            var names = await users.DisplayNamesAsync([ownerId], cancellationToken);

            return invite is not null && IsUsable(invite, now) && libraryName is not null && names.TryGetValue(ownerId, out var ownerName)
                ? new InvitePreviewDto(ownerName, libraryName, invite.Role, invite.ExpiresAt)
                : InviteErrors.Invalid;
        }
    }

    /// <summary>Not used, not revoked and not out of date.</summary>
    internal static bool IsUsable(LibraryInvite invite, DateTimeOffset now) =>
        invite.AcceptedAt is null && invite.RevokedAt is null && invite.ExpiresAt > now;
}

/// <summary>Writes the account of a person who joins by invite. Implemented by Infrastructure on ASP.NET Core Identity.</summary>
public interface IInviteAcceptance
{
    /// <summary>
    /// In one transaction: spends the invite (only if it is still unused, unrevoked and in date, decided in a single statement so two
    /// people finishing together can't both win), creates the user linked to <paramref name="identity"/>, and makes them a member with the
    /// invited role. Fails with <see cref="InviteErrors.Invalid"/> (or <c>AccountErrors.AlreadyLinked</c> when the identity was linked
    /// meanwhile) and changes nothing.
    /// </summary>
    Task<Result<LinkedLogin>> AcceptAsync(Guid inviteId, Guid userId, string displayName, ExternalIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Finding an invite that can still be used. Joining itself happens when the person comes back from Auth0 (<c>CompleteSignIn</c>).</summary>
public static class PlanInvite
{
    internal static async Task<LibraryInvite?> FindUsableAsync(IAppDbContext db, string? token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = InviteSecrets.Hash(token ?? string.Empty);
        var invite = await db.LibraryInvites.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        return invite is not null && GetInvitePreview.IsUsable(invite, now) ? invite : null;
    }
}
