using System.Globalization;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Catalog;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Infrastructure.Accounts.Deletion;

public static class AccountDeletionRegistration
{
    /// <summary>
    /// Registers the purge of deleted accounts: its job handler and the stores it uses. Needs <c>AddInfrastructure()</c>, <c>AddJobs()</c>,
    /// <c>AddAudit()</c> and the Identity services of sign-in. Other features add their own <see cref="ILibraryPurgeStep"/>s.
    /// </summary>
    public static IServiceCollection AddAccountPurge(this IServiceCollection services)
    {
        services.TryAddScoped<IDeletedAccounts, IdentityDeletedAccounts>();
        services.TryAddScoped<IPurgeBacklog, IdentityPurgeBacklog>();
        services.TryAddScoped<IAccountDeactivation, IdentityAccountDeactivation>();
        services.TryAddSingleton<IAccountFiles>(provider =>
            new FileAccountFiles(
                provider.GetRequiredService<IConfiguration>()[CatalogInfrastructure.CoversRootKey] ?? Path.Combine("data", "covers"),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<FileAccountFiles>>()));
        services.AddJobHandler<PurgeAccountPayload, PurgeAccount.Handler>(PurgeAccount.JobType);
        services.AddJobHandler<PurgeSweep.Payload, PurgeSweep.Handler>(PurgeSweep.JobType);
        services.AddRecurringJob(PurgeSweep.JobType, TimeSpan.FromDays(1));
        return services;
    }
}

/// <summary>Deleted accounts on ASP.NET Core Identity.</summary>
internal sealed class IdentityDeletedAccounts(AppDbContext db) : IDeletedAccounts
{
    /// <summary>What "Added by" says for a person who is gone.</summary>
    internal const string RemovedUserName = PurgeAccount.RemovedUserName;

    public Task<DateTimeOffset?> DeletedAtAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DeletedAt).SingleOrDefaultAsync(cancellationToken);

    public async Task ScrubAuditTrailAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Details hold ids only, but an id of the user is still them. The text is searched in both spellings.
        var dashed = userId.ToString("D", CultureInfo.InvariantCulture);
        var compact = userId.ToString("N", CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE audit_events SET details = NULL
            WHERE details IS NOT NULL AND (details::text ILIKE {"%" + dashed + "%"} OR details::text ILIKE {"%" + compact + "%"})
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE audit_events SET ip_hash = NULL WHERE actor_user_id = {userId}", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE audit_events SET entity_id = NULL WHERE entity_type = 'user' AND entity_id = {userId}", cancellationToken);
    }

    public async Task EraseAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeletedAt != null, cancellationToken))
        {
            return; // never erase an account that is still in use
        }

        var subject = userId.ToString("D", CultureInfo.InvariantCulture);
        await db.Set<CollectionMaxxing.Domain.Accounts.WebSession>().Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.IdempotencyKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserRoles.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserClaims.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Logins.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken); // their Auth0 identities may be linked afresh
        await db.UserTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        // The row stays: items they added to other libraries still point at it. It no longer holds anything about them.
        await db.Users.Where(u => u.Id == userId && u.DeletedAt != null).ExecuteUpdateAsync(
            set => set
                .SetProperty(u => u.DisplayName, RemovedUserName)
                .SetProperty(u => u.Email, (string?)null)
                .SetProperty(u => u.NormalizedEmail, (string?)null)
                .SetProperty(u => u.PhoneNumber, (string?)null)
                .SetProperty(u => u.PasswordHash, (string?)null)
                .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")),
            cancellationToken);

        // The purge job named the user in its payload and its dedupe key. This runs inside that job, which only marks itself done afterwards.
        var emptied = """{"key": null, "data": {}}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET payload = {emptied}::jsonb
            WHERE type = {PurgeAccount.JobType} AND payload -> 'data' ->> 'userId' = {subject}
            """, cancellationToken);
    }
}

/// <summary>Deleted accounts whose purge is overdue, found in the user table.</summary>
internal sealed class IdentityPurgeBacklog(AppDbContext db) : IPurgeBacklog
{
    public async Task<IReadOnlyList<Guid>> UsersAwaitingPurgeAsync(DateTimeOffset deletedBefore, CancellationToken cancellationToken)
    {
        // A purged user has no place in any library, no linked identity, and the tombstone's name. Whatever is left of those means the purge isn't finished.
        // (Re-queueing a user who is in fact purged would be harmless: the purge finds nothing to do.)
        // An export record is kept while its file is stuck; it names the person in its payload.
        var exportType = BuildExport.JobType;
        var withExports = await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT (payload -> 'data' ->> 'userId')::uuid AS "Value"
            FROM jobs
            WHERE type = {exportType} AND payload -> 'data' ->> 'userId' IS NOT NULL
            """).ToListAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt != null && u.DeletedAt <= deletedBefore)
            .Where(u => u.DisplayName != PurgeAccount.RemovedUserName
                || db.LibraryMembers.IgnoreQueryFilters().Any(m => m.UserId == u.Id)
                || db.Logins.Any(l => l.UserId == u.Id)
                || withExports.Contains(u.Id))
            .OrderBy(u => u.DeletedAt)
            .Select(u => u.Id)
            .Take(500) // a home server has a handful; the rest are picked up tomorrow
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Deactivates an account in one transaction that also looks at who belongs to the libraries it owns.</summary>
internal sealed class IdentityAccountDeactivation(AppDbContext db, TimeProvider time) : IAccountDeactivation
{
    public async Task<bool> DeactivateUnlessLibraryHasMembersAsync(Guid userId, Func<CancellationToken, Task> whileDeactivating, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The update locks the user's row until the transaction ends. An invite being accepted into this person's library locks the same row
        // first (IdentityInviteAcceptance), so one of the two waits for the other: either the member is already there when the members are
        // counted below, or the invite finds the owner deleted and is refused.
        await db.Users
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletedAt, now), cancellationToken);

        var hasMembers = await db.LibraryMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.Role == LibraryRole.Owner)
            .AnyAsync(
                owned => db.LibraryMembers.IgnoreQueryFilters().Any(other => other.LibraryId == owned.LibraryId && other.UserId != userId),
                cancellationToken);
        if (hasMembers)
        {
            return false; // leaving the transaction without a commit puts the user back as they were
        }

        await whileDeactivating(cancellationToken); // an exception leaves without a commit: the user is put back as they were
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// Deletes cover and photo files from the PC's folder. A path can never point outside that folder. A file that is already gone is fine. A file
/// that can't be deleted right now (a virus scanner has it open, say) is tried a few times; if it still can't be deleted it is reported in the
/// log, by path, and the purge carries on, because one stuck file must not stop a person's identity from being erased. The purge keeps the
/// record of a stuck file (<see cref="TryDeleteAsync"/> says false) and the daily sweep tries again until it is gone.
/// </summary>
public sealed class FileAccountFiles : IAccountFiles
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public FileAccountFiles(string root, TimeProvider? time = null, ILogger? logger = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task DeleteAsync(string blobPath, CancellationToken cancellationToken) => await TryDeleteAsync(blobPath, cancellationToken);

    /// <summary>True when the file is gone (or was never there or is not a safe path); false when it is stuck and still on disk.</summary>
    public async Task<bool> TryDeleteAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (!BlobPaths.IsSafe(blobPath))
        {
            return true;
        }

        var full = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return true;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(full); // no error if it is already gone
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true; // the folder is gone too: nothing to delete
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RetryDelays.Length)
                {
                    _logger.LogWarning(exception, "Could not delete the file {BlobPath} while purging an account; it is left on disk and will be tried again.", blobPath);
                    return false;
                }

                await Task.Delay(RetryDelays[attempt], _time, cancellationToken);
            }
        }
    }
}
