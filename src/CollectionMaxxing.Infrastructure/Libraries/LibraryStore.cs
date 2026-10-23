using CollectionMaxxing.Application.Libraries;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Libraries;

public static class LibraryRegistration
{
    /// <summary>The store behind creating a library.</summary>
    public static IServiceCollection AddLibraryStores(this IServiceCollection services)
    {
        services.TryAddScoped<ILibraryStore, LibraryStore>();
        return services;
    }
}

/// <summary>Creates libraries. One transaction, with the owner's account locked, so the limit holds when two creates race.</summary>
internal sealed class LibraryStore(AppDbContext db) : ILibraryStore
{
    public async Task<Result> CreateOwnedAsync(Library library, Guid ownerId, int maxOwned, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Every create by this person waits here for the one before it, so the count below can't go stale before the insert.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE id = {ownerId} FOR UPDATE", cancellationToken);

        // Memberships across every library (the filter would show only the caller's, which is the same set, but this must not depend on it).
        var owned = await db.LibraryMembers.IgnoreQueryFilters()
            .CountAsync(m => m.UserId == ownerId && m.Role == LibraryRole.Owner, cancellationToken);
        if (owned >= maxOwned)
        {
            return Result.Failure(LibraryErrors.LimitReached(maxOwned));
        }

        db.Libraries.Add(library);
        db.LibraryMembers.Add(new LibraryMember(library.Id, ownerId, LibraryRole.Owner, library.CreatedAt));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
