using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Invites;

public static class InviteRegistration
{
    /// <summary>The stores behind invites and members: joining by invite, and the people behind user ids.</summary>
    public static IServiceCollection AddInviteStores(this IServiceCollection services)
    {
        services.TryAddScoped<IInviteAcceptance, InviteAcceptance>();
        services.TryAddScoped<IUserDirectory, IdentityUserDirectory>();
        return services;
    }
}

/// <summary>Names and the state of accounts, on ASP.NET Core Identity's user table.</summary>
internal sealed class IdentityUserDirectory(AppDbContext db) : IUserDirectory
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

    public Task<AccountProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AccountProfile(u.DisplayName, u.Email))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> IsDeactivatedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.DeletedAt != null, cancellationToken);

    public async Task<IReadOnlyList<Guid>> FindActiveByEmailAsync(string email, CancellationToken cancellationToken)
    {
        // Identity normalizes emails to upper case (UpperInvariantLookupNormalizer); the sign-ins keep theirs as Auth0 gave them.
        var normalized = email.Trim().ToUpperInvariant();
        var pattern = email.Trim().Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
        var fromLogins = db.Logins.Where(l => l.Email != null && EF.Functions.ILike(l.Email, pattern, @"\")).Select(l => l.UserId);
        return await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && (u.NormalizedEmail == normalized || fromLogins.Contains(u.Id)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Adds a signed-in account to a library by invite. One transaction: it all happens, or none of it.</summary>
internal sealed class InviteAcceptance(AppDbContext db, TimeProvider time) : IInviteAcceptance
{
    public async Task<Result<LibraryRole>> AcceptAsync(Guid inviteId, Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Spend the invite first, in one statement that also checks it is still good. Two people accepting at the same moment both run
        // this: Postgres makes the second wait for the first, re-reads the row, and finds it spent.
        var spent = await db.LibraryInvites.IgnoreQueryFilters()
            .Where(i => i.Id == inviteId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.AcceptedAt, now).SetProperty(i => i.AcceptedBy, userId), cancellationToken);
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

        db.LibraryMembers.Add(new LibraryMember(invite.LibraryId, userId, invite.Role, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return InviteErrors.AlreadyMember; // joined at the same moment by another invite; this one stays unspent
        }

        await transaction.CommitAsync(cancellationToken);
        return invite.Role;
    }
}
