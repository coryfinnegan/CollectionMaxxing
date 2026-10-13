using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>Creates accounts with ASP.NET Core Identity. Each operation is one transaction: it all happens, or none of it.</summary>
internal sealed class IdentityAccountStore(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IIdGenerator ids,
    TimeProvider time) : IAccountStore
{
    /// <summary>The platform administrator role (same name as <c>Roles.Admin</c> in the API). The owner who sets the server up is the admin.</summary>
    private const string AdminRole = "admin";

    public async Task<Result<SignedInDto>> CreateOwnerAsync(
        Guid userId,
        string displayName,
        PasskeyCredential passkey,
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
        Check(await users.AddOrUpdatePasskeyAsync(user, ToInfo(passkey)));

        if (!await roles.RoleExistsAsync(AdminRole))
        {
            Check(await roles.CreateAsync(new IdentityRole<Guid>(AdminRole)));
        }

        Check(await users.AddToRoleAsync(user, AdminRole));

        var library = new Library(ids.NewId(), $"{displayName}'s library", now);
        db.Libraries.Add(library);
        db.LibraryMembers.Add(new LibraryMember(library.Id, userId, LibraryRole.Owner, now));
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SignedInDto(userId, displayName, library.Id);
    }

    public async Task<Result<SignedInDto>> AddPasskeyAsync(Guid userId, PasskeyCredential passkey, Guid? codeId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (codeId is { } id && await SpendCodeAsync(id, now, cancellationToken) is { } codeError)
        {
            return codeError;
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || user.DeletedAt is not null)
        {
            return AccountErrors.NoOwner;
        }

        Check(await users.AddOrUpdatePasskeyAsync(user, ToInfo(passkey)));
        var libraryId = await db.LibraryMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.Role)
            .Select(m => (Guid?)m.LibraryId)
            .FirstOrDefaultAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SignedInDto(userId, user.DisplayName, libraryId);
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
