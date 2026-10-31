using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries;
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

            if (TextRules.Check(name, "name") is { } badText)
            {
                return badText;
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
            var code = InviteSecrets.NewCode();
            var invite = new LibraryInvite
            {
                Id = ids.NewId(),
                LibraryId = command.LibraryId,
                Role = role,
                Name = name,
                TokenHash = InviteSecrets.Hash(token),
                CodeHash = InviteSecrets.HashCode(InviteSecrets.NormalizeCode(code)!),
                CreatedBy = userId,
                CreatedAt = now,
                ExpiresAt = now + InviteSecrets.Lifetime,
            };
            db.LibraryInvites.Add(invite);
            await db.SaveChangesAsync(cancellationToken);

            var link = $"{server.Value}/join/{token}";
            return new CreatedInviteDto(ToDto(invite), link, link, code);
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

/// <summary>
/// Use case: what the join page shows (<c>GET /api/v1/invites/{token}</c>, and <c>POST /api/v1/invites:preview</c> with a link, a token or
/// a code). Nobody learns anything about an invite that isn't usable.
/// </summary>
public static class GetInvitePreview
{
    public sealed class Handler(IAppDbContext db, IUserDirectory users, TimeProvider time)
    {
        public Task<Result<InvitePreviewDto>> HandleAsync(string? token, CancellationToken cancellationToken) =>
            HandleAsync(InviteKey.ForToken(token), cancellationToken);

        public async Task<Result<InvitePreviewDto>> HandleAsync(InviteKey key, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();

            // An unknown, an expired, a used and a revoked invite must cost the same and answer the same: every one runs the same
            // lookups, and only the last line decides. (The filters are off: the caller isn't a member of the library yet.)
            var invite = await FindAsync(db, key, cancellationToken);
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

    /// <summary>The invite <paramref name="key"/> names, in any library and in any state, or null.</summary>
    internal static Task<LibraryInvite?> FindAsync(IAppDbContext db, InviteKey key, CancellationToken cancellationToken) =>
        key.IsCode
            ? db.LibraryInvites.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.CodeHash == key.Hash, cancellationToken)
            : db.LibraryInvites.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == key.Hash, cancellationToken);
}

/// <summary>Adds a signed-in account to a library by invite. Implemented by Infrastructure.</summary>
public interface IInviteAcceptance
{
    /// <summary>
    /// In one transaction: spends the invite (only if it is still unused, unrevoked and in date, decided in a single statement so two
    /// people accepting together can't both win) and makes <paramref name="userId"/> a member with the invited role, which is never Owner.
    /// Fails with <see cref="InviteErrors.Invalid"/> or <see cref="InviteErrors.AlreadyMember"/>, and then changes nothing.
    /// </summary>
    Task<Result<LibraryRole>> AcceptAsync(Guid inviteId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Use case: <c>POST /api/v1/invites:accept</c>. A signed-in account joins a library with an invite link, token or code, and gets exactly
/// the invited role. Unknown, expired, revoked and used invites all get the same answer.
/// </summary>
public static class AcceptInvite
{
    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IInviteAcceptance acceptance, TimeProvider time)
    {
        public async Task<Result<LibrarySummaryDto>> HandleAsync(string? invite, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId ?? throw new InvalidOperationException("Accepting an invite needs a signed-in caller; the endpoint requires authorization.");
            var found = await GetInvitePreview.FindAsync(db, InviteKey.Parse(invite), cancellationToken);
            if (found is null || !GetInvitePreview.IsUsable(found, time.GetUtcNow()))
            {
                return InviteErrors.Invalid;
            }

            // Already in the library: say so, and leave the invite for whoever it was really for.
            if (currentUser.RoleIn(found.LibraryId) is not null)
            {
                return InviteErrors.AlreadyMember;
            }

            var accepted = await acceptance.AcceptAsync(found.Id, userId, cancellationToken);
            if (!accepted.IsSuccess)
            {
                return accepted.Error!;
            }

            var name = await db.Libraries.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.Id == found.LibraryId).Select(l => l.Name).FirstAsync(cancellationToken);
            return new LibrarySummaryDto(found.LibraryId, name, accepted.Value);
        }
    }
}
