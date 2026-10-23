using System.Reflection;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>
/// The one database context: ASP.NET Core Identity's tables (users, roles) and ours. It doesn't list
/// our entities' mapping: each feature keeps its entity configuration in its own folder (an
/// <c>IEntityTypeConfiguration&lt;T&gt;</c>), found by <c>ApplyConfigurationsFromAssembly</c>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    private static readonly MethodInfo FilterByLibraryMethod =
        typeof(AppDbContext).GetMethod(nameof(FilterByLibrary), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public DbSet<Library> Libraries => Set<Library>();

    public DbSet<LibraryMember> LibraryMembers => Set<LibraryMember>();

    public DbSet<LibraryInvite> LibraryInvites => Set<LibraryInvite>();

    public DbSet<UserLogin> Logins => Set<UserLogin>();

    public DbSet<SetupCode> SetupCodes => Set<SetupCode>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Item> Items => Set<Item>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ItemTag> ItemTags => Set<ItemTag>();

    public DbSet<ItemFeaturePrint> ItemFeaturePrints => Set<ItemFeaturePrint>();

    public DbSet<ItemTombstone> ItemTombstones => Set<ItemTombstone>();

    public DbSet<Work> Works => Set<Work>();

    public DbSet<Release> Releases => Set<Release>();

    public DbSet<ReleaseIdentifier> ReleaseIdentifiers => Set<ReleaseIdentifier>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Platform> Platforms => Set<Platform>();

    public DbSet<ProviderCacheEntry> ProviderCache => Set<ProviderCacheEntry>();

    public DbSet<ProviderQuota> ProviderQuotas => Set<ProviderQuota>();

    public DbSet<ScanSession> ScanSessions => Set<ScanSession>();

    public DbSet<ScanSnap> ScanSnaps => Set<ScanSnap>();

    public DbSet<ScanCandidate> ScanCandidates => Set<ScanCandidate>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<MatchFeedback> MatchFeedback => Set<MatchFeedback>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    // Read inside the query filters below. EF turns it into a query parameter, so each query uses the
    // caller's libraries at that moment.
    private IReadOnlyList<Guid> CurrentLibraryIds => currentUser.LibraryIds;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // Identity's tables

        // Auth0 signs people in (ADR-025): Identity keeps users and roles, but its passkeys and external logins are not used. Our own
        // user_logins table (UserLogin) links Auth0 identities to users.
        builder.Ignore<IdentityUserPasskey<Guid>>();
        builder.Ignore<IdentityUserLogin<Guid>>();
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        IdentityModel.Configure(builder);

        // Search: pg_trgm for fuzzy title matching, citext for names that compare case-insensitively.
        builder.HasPostgresExtension("pg_trgm");
        builder.HasPostgresExtension("citext");

        // Optimistic concurrency: every editable row carries Postgres' xmin, so a stale save fails instead of overwriting.
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(IConcurrencyVersioned).IsAssignableFrom(t.ClrType)))
        {
            builder.Entity(entityType.ClrType)
                .Property(nameof(IConcurrencyVersioned.Version))
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        // A library is visible to its members; everything inside it is filtered by library_id.
        builder.Entity<Library>().HasQueryFilter(l => CurrentLibraryIds.Contains(l.Id));
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(ILibraryScoped).IsAssignableFrom(t.ClrType)))
        {
            FilterByLibraryMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
        }

        // Catalogue rows are shared, except what a library made for itself: those are visible to its members only.
        builder.Entity<Release>().HasQueryFilter(r => r.OwnerLibraryId == null || CurrentLibraryIds.Contains(r.OwnerLibraryId.Value));
        builder.Entity<Asset>().HasQueryFilter(a => a.OwnerLibraryId == null || CurrentLibraryIds.Contains(a.OwnerLibraryId.Value));
    }

    private void FilterByLibrary<T>(ModelBuilder builder)
        where T : class, ILibraryScoped =>
        builder.Entity<T>().HasQueryFilter(e => CurrentLibraryIds.Contains(e.LibraryId));
}
