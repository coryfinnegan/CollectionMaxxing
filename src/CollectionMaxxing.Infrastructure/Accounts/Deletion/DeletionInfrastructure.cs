using System.Globalization;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Infrastructure.Catalog;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;

namespace CollectionMaxxing.Infrastructure.Accounts.Deletion;

public static class AccountDeletionRegistration
{
    /// <summary>
    /// Registers the purge of deleted accounts: its job handler and the stores it uses. Needs <c>AddInfrastructure()</c>, <c>AddJobs()</c>,
    /// <c>AddAudit()</c> and the OpenIddict services of sign-in. Other features add their own <see cref="ILibraryPurgeStep"/>s.
    /// </summary>
    public static IServiceCollection AddAccountPurge(this IServiceCollection services)
    {
        services.TryAddScoped<IDeletedAccounts, IdentityDeletedAccounts>();
        services.TryAddSingleton<IAccountFiles>(provider =>
            new FileAccountFiles(provider.GetRequiredService<IConfiguration>()[CatalogInfrastructure.CoversRootKey] ?? Path.Combine("data", "covers")));
        services.AddJobHandler<PurgeAccountPayload, PurgeAccount.Handler>(PurgeAccount.JobType);
        return services;
    }
}

/// <summary>Deleted accounts on ASP.NET Core Identity and OpenIddict.</summary>
internal sealed class IdentityDeletedAccounts(
    AppDbContext db,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens) : IDeletedAccounts
{
    /// <summary>What "Added by" says for a person who is gone.</summary>
    internal const string RemovedUserName = "Removed user";

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

        // Phones: every token and every authorization of the user (they were revoked when the account was deleted; now the rows go).
        var subject = userId.ToString("D", CultureInfo.InvariantCulture);
        var found = new List<object>();
        await foreach (var token in tokens.FindBySubjectAsync(subject, cancellationToken))
        {
            found.Add(token);
        }

        foreach (var token in found)
        {
            await tokens.DeleteAsync(token, cancellationToken);
        }

        found.Clear();
        await foreach (var authorization in authorizations.FindBySubjectAsync(subject, cancellationToken))
        {
            found.Add(authorization);
        }

        foreach (var authorization in found)
        {
            await authorizations.DeleteAsync(authorization, cancellationToken);
        }

        await db.DevicePairings.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.IdempotencyKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<IdentityUserPasskey<Guid>>().Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserRoles.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserClaims.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken);
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

/// <summary>Deletes cover and photo files from the PC's folder. A path can never point outside that folder.</summary>
internal sealed class FileAccountFiles : IAccountFiles
{
    private readonly string _root;

    public FileAccountFiles(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public Task DeleteAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (!BlobPaths.IsSafe(blobPath))
        {
            return Task.CompletedTask;
        }

        var full = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(full); // no error if it is already gone
        }
        catch (DirectoryNotFoundException)
        {
            // the folder is gone too: nothing to delete
        }

        return Task.CompletedTask;
    }
}
