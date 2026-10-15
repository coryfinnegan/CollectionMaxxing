using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Identity;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Invites;

public static class InviteRegistration
{
    /// <summary>The stores behind invites and members: joining by invite, and the people behind user ids.</summary>
    public static IServiceCollection AddInviteStores(this IServiceCollection services)
    {
        services.TryAddScoped<IInviteAcceptance, IdentityInviteAcceptance>();
        services.TryAddScoped<IUserDirectory, IdentityUserDirectory>();
        return services;
    }
}

/// <summary>Names and deactivation of accounts, on ASP.NET Core Identity's user table.</summary>
internal sealed class IdentityUserDirectory(AppDbContext db, TimeProvider time) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    public async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        // "Deleted" is how the rest of the system already says "can't sign in": the passkey login, the pairing grant, the refresh grant
        // and every authenticated request check it.
        await db.Users
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletedAt, now), cancellationToken);
    }

    public Task<bool> IsDeactivatedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.DeletedAt != null, cancellationToken);
}

/// <summary>Joins a library by invite with ASP.NET Core Identity. One transaction: it all happens, or none of it.</summary>
internal sealed class IdentityInviteAcceptance(AppDbContext db, UserManager<AppUser> users, TimeProvider time) : IInviteAcceptance
{
    public async Task<Result<SignedInDto>> AcceptAsync(
        Guid inviteId,
        Guid userId,
        string displayName,
        PasskeyCredential passkey,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Spend the invite first, in one statement that also checks it is still good. Two people finishing at the same moment both
        // run this: Postgres makes the second wait for the first, re-reads the row, and finds it spent.
        var spent = await db.LibraryInvites.IgnoreQueryFilters()
            .Where(i => i.Id == inviteId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.AcceptedAt, now), cancellationToken);
        if (spent != 1)
        {
            return InviteErrors.Invalid; // the transaction is disposed without a commit, so nothing is kept
        }

        var invite = await db.LibraryInvites.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == inviteId, cancellationToken);
        if (invite.Role is not (LibraryRole.Editor or LibraryRole.Viewer))
        {
            return InviteErrors.Invalid; // can't happen (the table refuses it), but an invite must never make an owner
        }

        // Nobody joins the library of an owner who has deleted their account: it would be left with members and no owner at day 30.
        // The owner's row is locked (FOR SHARE) until this transaction ends. Deleting an account updates that row, so it waits for us and then
        // counts the new member (and refuses), or it got there first and we find the owner deleted here. Either way no member slips in between.
        var ownerDeleted = await db.Database.SqlQuery<bool>($"""
            SELECT (u.deleted_at IS NOT NULL) AS "Value"
            FROM library_members m
            JOIN users u ON u.id = m.user_id
            WHERE m.library_id = {invite.LibraryId} AND m.role = 'owner'
            FOR SHARE OF u
            """).ToListAsync(cancellationToken);
        if (ownerDeleted.Contains(true))
        {
            return InviteErrors.Invalid;
        }

        var user = new AppUser
        {
            Id = userId,
            UserName = userId.ToString("N"),
            DisplayName = displayName,
            CreatedAt = now,
        };
        Check(await users.CreateAsync(user));
        Check(await users.AddOrUpdatePasskeyAsync(user, ToInfo(passkey)));

        db.LibraryMembers.Add(new LibraryMember(invite.LibraryId, userId, invite.Role, now));
        await db.SaveChangesAsync(cancellationToken);
        await db.LibraryInvites.IgnoreQueryFilters()
            .Where(i => i.Id == inviteId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.AcceptedBy, userId), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SignedInDto(userId, displayName, invite.LibraryId);
    }

    private static UserPasskeyInfo ToInfo(PasskeyCredential passkey) =>
        new(
            passkey.CredentialId,
            passkey.PublicKey,
            passkey.CreatedAt,
            passkey.SignCount,
            [.. passkey.Transports],
            passkey.IsUserVerified,
            passkey.IsBackupEligible,
            passkey.IsBackedUp,
            passkey.AttestationObject,
            passkey.ClientDataJson)
        { Name = passkey.Name };

    // An Identity failure here is a bug or a broken database, not something the caller can fix: throw so the transaction rolls back.
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Identity refused the change: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        }
    }
}
