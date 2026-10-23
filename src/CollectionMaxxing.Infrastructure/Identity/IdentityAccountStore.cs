using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>Creates accounts and links identities with ASP.NET Core Identity. Each operation is one transaction: it all happens, or none of it.</summary>
internal sealed class IdentityAccountStore(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IIdGenerator ids,
    TimeProvider time) : IAccountStore
{
    /// <summary>The platform administrator role (same name as <c>Roles.Admin</c> in the API). The owner who sets the server up is the admin.</summary>
    private const string AdminRole = "admin";

    public async Task<Result<LinkedLogin>> CreateOwnerAsync(
        Guid userId,
        string displayName,
        ExternalIdentity identity,
        Guid setupCodeId,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await SpendCodeAsync(setupCodeId, now, cancellationToken) is { } codeError)
        {
            return codeError;
        }

        if (await IssueCode.OwnerExistsAsync(db, cancellationToken))
        {
            return AccountErrors.SetupClosed;
        }

        var user = new AppUser
        {
            Id = userId,
            UserName = userId.ToString("N"),
            DisplayName = displayName,
            CreatedAt = now,
        };
        Check(await users.CreateAsync(user));

        if (!await roles.RoleExistsAsync(AdminRole))
        {
            Check(await roles.CreateAsync(new IdentityRole<Guid>(AdminRole)));
        }

        Check(await users.AddToRoleAsync(user, AdminRole));

        var library = new Library(ids.NewId(), $"{displayName}'s library", now);
        db.Libraries.Add(library);
        db.LibraryMembers.Add(new LibraryMember(library.Id, userId, LibraryRole.Owner, now));
        var login = NewLogin(userId, identity, now);
        db.Logins.Add(login);
        if (!await TrySaveAsync(cancellationToken))
        {
            return AccountErrors.AlreadyLinked;
        }

        await transaction.CommitAsync(cancellationToken);
        return new LinkedLogin(login.Id, userId);
    }

    public async Task<Result<LinkedLogin>> LinkAsync(Guid userId, ExternalIdentity identity, Guid? codeId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (codeId is { } id && await SpendCodeAsync(id, now, cancellationToken) is { } codeError)
        {
            return codeError;
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken))
        {
            return AccountErrors.NoOwner;
        }

        var existing = await db.Logins.AsNoTracking().FirstOrDefaultAsync(l => l.Subject == identity.Subject, cancellationToken);
        if (existing is not null)
        {
            if (existing.UserId != userId)
            {
                return AccountErrors.AlreadyLinked; // the transaction is disposed without a commit, so the code stays unspent
            }

            await transaction.CommitAsync(cancellationToken);
            return new LinkedLogin(existing.Id, userId);
        }

        var login = NewLogin(userId, identity, now);
        db.Logins.Add(login);
        if (!await TrySaveAsync(cancellationToken))
        {
            return AccountErrors.AlreadyLinked;
        }

        await transaction.CommitAsync(cancellationToken);
        return new LinkedLogin(login.Id, userId);
    }

    public async Task<Result> UnlinkUnlessLastAsync(Guid userId, Guid loginId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Every change to a user's identities locks the user's row first, so the count below can't go stale before the delete.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);
        var mine = await db.Logins.Where(l => l.UserId == userId).Select(l => l.Id).ToListAsync(cancellationToken);
        if (!mine.Contains(loginId))
        {
            return Result.Failure(AccountErrors.SignInMethodNotFound);
        }

        if (mine.Count < 2)
        {
            return Result.Failure(AccountErrors.LastSignInMethod);
        }

        await db.Logins.Where(l => l.Id == loginId && l.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    internal static UserLogin NewLogin(Guid userId, ExternalIdentity identity, DateTimeOffset now)
    {
        var email = identity.Email?.Trim();
        return new UserLogin
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Provider = UserLogin.Auth0Provider,
            Subject = identity.Subject,
            Email = string.IsNullOrEmpty(email) || email.Length > UserLogin.MaxEmailLength ? null : email,
            CreatedAt = now,
            LastUsedAt = now,
        };
    }

    /// <summary>Saves; false when the identity was linked by somebody else at the same moment (the unique index on the subject said no).</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Marks the code used, but only if it still is unused and in date: one statement, so two people finishing at the same
    /// moment can't both win. Returns why not when it fails.
    /// </summary>
    private async Task<DomainError?> SpendCodeAsync(Guid codeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spent = await db.SetupCodes
            .Where(c => c.Id == codeId && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedAt, now), cancellationToken);
        if (spent == 1)
        {
            return null;
        }

        var stored = await db.SetupCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == codeId, cancellationToken);
        return stored is null ? AccountErrors.CodeInvalid : stored.UsedAt is not null ? AccountErrors.CodeUsed : AccountErrors.CodeExpired;
    }

    // An Identity failure here is a bug or a broken database, not something the caller can fix: throw so the transaction rolls back.
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Identity refused the change: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        }
    }
}
