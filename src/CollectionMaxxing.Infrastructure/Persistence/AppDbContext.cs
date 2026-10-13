using System.Reflection;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>
/// The one database context. It doesn't list tables: each feature keeps its entity configuration in its own
/// folder (an <c>IEntityTypeConfiguration&lt;T&gt;</c>), found by <c>ApplyConfigurationsFromAssembly</c>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options), IAppDbContext
{
    private static readonly MethodInfo FilterByLibraryMethod =
        typeof(AppDbContext).GetMethod(nameof(FilterByLibrary), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public DbSet<Library> Libraries => Set<Library>();

    public DbSet<LibraryMember> LibraryMembers => Set<LibraryMember>();

    public DbSet<Location> Locations => Set<Location>();

    // Read inside the query filters below. EF turns it into a query parameter, so each query uses the
    // caller's libraries at that moment.
    private IReadOnlyList<Guid> CurrentLibraryIds => currentUser.LibraryIds;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // A library is visible to its members; everything inside it is filtered by library_id.
        modelBuilder.Entity<Library>().HasQueryFilter(l => CurrentLibraryIds.Contains(l.Id));
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ILibraryScoped).IsAssignableFrom(t.ClrType)))
        {
            FilterByLibraryMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void FilterByLibrary<T>(ModelBuilder modelBuilder)
        where T : class, ILibraryScoped =>
        modelBuilder.Entity<T>().HasQueryFilter(e => CurrentLibraryIds.Contains(e.LibraryId));
}
