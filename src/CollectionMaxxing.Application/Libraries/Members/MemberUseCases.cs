using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Libraries.Members;

/// <summary>
/// Use case: the people in a library. Everyone in the library sees names and roles; the owner also sees each member's paired
/// devices and when they were last seen (docs/06, W3).
/// </summary>
public static class ListMembers
{
    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IUserDirectory users, IDeviceAuthorizations devices)
    {
        public async Task<Page<MemberDto>> HandleAsync(Guid libraryId, CancellationToken cancellationToken)
        {
            // The owner first, then by when they joined. (Roles are stored as text, so the order by role is done here, not in SQL.)
            var members = (await db.LibraryMembers.AsNoTracking()
                    .Where(m => m.LibraryId == libraryId)
                    .ToListAsync(cancellationToken))
                .OrderByDescending(m => m.Role).ThenBy(m => m.JoinedAt).ThenBy(m => m.UserId)
                .ToList();
            var names = await users.DisplayNamesAsync([.. members.Select(m => m.UserId)], cancellationToken);
            var callerIsOwner = currentUser.RoleIn(libraryId) == LibraryRole.Owner;

            var items = new List<MemberDto>();
            foreach (var member in members)
            {
                IReadOnlyList<string>? deviceNames = null;
                DateTimeOffset? lastSeen = null;
                if (callerIsOwner)
                {
                    var sessions = await devices.ListAsync(member.UserId, cancellationToken);
                    var active = sessions.Where(s => !s.IsRevoked).ToList();
                    deviceNames = [.. active.OrderBy(s => s.CreatedAt).Select(s => s.DeviceName)];
                    lastSeen = active.Count == 0 ? null : active.Max(s => s.LastSeenAt ?? s.CreatedAt);
                }

                items.Add(new MemberDto(member.UserId, names.GetValueOrDefault(member.UserId, string.Empty), member.Role, member.JoinedAt, deviceNames, lastSeen));
            }

            return new Page<MemberDto>(items);
        }
    }
}

/// <summary>Use case: the owner changes a member's role. It takes effect on that member's next request.</summary>
public static class UpdateMember
{
    public sealed record Command(Guid LibraryId, Guid UserId, LibraryRole? Role);

    public sealed class Handler(IAppDbContext db, IUserDirectory users)
    {
        public async Task<Result<MemberDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.Role is not { } role)
            {
                return MemberErrors.RoleRequired;
            }

            if (role is not (LibraryRole.Editor or LibraryRole.Viewer))
            {
                return MemberErrors.RoleNotAssignable;
            }

            var member = await db.LibraryMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.LibraryId == command.LibraryId && m.UserId == command.UserId, cancellationToken);
            if (member is null)
            {
                return MemberErrors.NotFound;
            }

            if (member.Role == LibraryRole.Owner)
            {
                return MemberErrors.OwnerIsFixed;
            }

            // One statement, so a request that arrives a moment later sees the new role. (The owner's row is excluded again here in
            // case they became the owner between the read and now.)
            var changed = await db.LibraryMembers
                .Where(m => m.LibraryId == command.LibraryId && m.UserId == command.UserId && m.Role != LibraryRole.Owner)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, role), cancellationToken);
            if (changed == 0)
            {
                return MemberErrors.NotFound;
            }

            var names = await users.DisplayNamesAsync([command.UserId], cancellationToken);
            return new MemberDto(command.UserId, names.GetValueOrDefault(command.UserId, string.Empty), role, member.JoinedAt);
        }
    }
}

/// <summary>
/// Use case: the owner removes a member. They lose the library, their account is deactivated (it has no library left), and every device
/// and session of theirs stops working at once. The items they added stay.
/// </summary>
public static class RemoveMember
{
    public sealed record Command(Guid LibraryId, Guid UserId);

    public sealed class Handler(IAppDbContext db, IMembershipReader memberships, IUserDirectory users, IDeviceAuthorizations devices)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var member = await db.LibraryMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.LibraryId == command.LibraryId && m.UserId == command.UserId, cancellationToken);
            if (member is null)
            {
                return Result.Failure(MemberErrors.NotFound);
            }

            if (member.Role == LibraryRole.Owner)
            {
                return Result.Failure(MemberErrors.OwnerIsFixed);
            }

            var removed = await db.LibraryMembers
                .Where(m => m.LibraryId == command.LibraryId && m.UserId == command.UserId && m.Role != LibraryRole.Owner)
                .ExecuteDeleteAsync(cancellationToken);
            if (removed == 0)
            {
                return Result.Failure(MemberErrors.NotFound);
            }

            // With no library left the account has no use. Deactivating it is what makes the web session and the phone's access token
            // fail on the very next call; revoking the phones ends their refresh tokens too.
            if ((await memberships.GetMembershipsAsync(command.UserId, cancellationToken)).Count == 0)
            {
                await users.DeactivateAsync(command.UserId, cancellationToken);
            }

            await devices.RevokeAllAsync(command.UserId, cancellationToken);
            return Result.Success();
        }
    }
}
