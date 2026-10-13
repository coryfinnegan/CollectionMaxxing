using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Common;

/// <summary>
/// The application's view of the database. Handlers depend on this port; Infrastructure's
/// <c>AppDbContext</c> implements it. A new feature adds its <c>DbSet</c> here.
/// </summary>
public interface IAppDbContext
{
    DbSet<Library> Libraries { get; }

    DbSet<LibraryMember> LibraryMembers { get; }

    DbSet<Location> Locations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
