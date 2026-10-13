using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Libraries;

/// <summary>Use case: the libraries the caller belongs to, with their role in each.</summary>
public static class ListLibraries
{
    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser)
    {
        public async Task<Page<LibrarySummaryDto>> HandleAsync(CancellationToken cancellationToken)
        {
            var libraries = await db.Libraries.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id).ToListAsync(cancellationToken);
            var items = libraries
                .Select(l => currentUser.RoleIn(l.Id) is { } role ? new LibrarySummaryDto(l.Id, l.Name, role) : null)
                .OfType<LibrarySummaryDto>()
                .ToList();
            return new Page<LibrarySummaryDto>(items);
        }
    }
}

/// <summary>Use case: one library the caller belongs to.</summary>
public static class GetLibrary
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser)
    {
        public async Task<Result<LibraryDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var library = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == query.LibraryId, cancellationToken);
            return library is not null && currentUser.RoleIn(library.Id) is { } role
                ? new LibraryDto(library.Id, library.Name, library.CreatedAt, role)
                : Errors.LibraryNotFound;
        }
    }
}

/// <summary>Use case: rename a library (a partial update; the endpoint allows owners only).</summary>
public static class UpdateLibrary
{
    public sealed record Command(Guid LibraryId, string? Name);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser)
    {
        public async Task<Result<LibraryDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == command.LibraryId, cancellationToken);
            if (library is null || currentUser.RoleIn(library.Id) is not { } role)
            {
                return Errors.LibraryNotFound;
            }

            if (command.Name is not null)
            {
                var renamed = library.Rename(command.Name);
                if (renamed.Error is { } error)
                {
                    return error;
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            return new LibraryDto(library.Id, library.Name, library.CreatedAt, role);
        }
    }
}
