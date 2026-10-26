using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Common;

/// <summary>
/// The application's view of the database. Handlers depend on this port; Infrastructure's
/// <c>AppDbContext</c> implements it. Every table of the v1 schema is listed here, so a feature uses the set it needs
/// without touching this file. (Users and roles belong to ASP.NET Core Identity, which the ID stream reaches
/// through its own services, not through this port.)
/// </summary>
public interface IAppDbContext
{
    // Libraries and sign-in
    DbSet<Library> Libraries { get; }

    DbSet<LibraryMember> LibraryMembers { get; }

    DbSet<LibraryInvite> LibraryInvites { get; }

    /// <summary>Auth0 identities linked to users (<c>user_logins</c>).</summary>
    DbSet<UserLogin> Logins { get; }

    DbSet<SetupCode> SetupCodes { get; }

    /// <summary>The iPhones signed in with Auth0 that registered themselves (<c>app_sign_ins</c>).</summary>
    DbSet<AppSignIn> AppSignIns { get; }

    // Your stuff (library-scoped)
    DbSet<Location> Locations { get; }

    DbSet<Item> Items { get; }

    DbSet<Tag> Tags { get; }

    DbSet<ItemTag> ItemTags { get; }

    DbSet<ItemFeaturePrint> ItemFeaturePrints { get; }

    DbSet<ItemTombstone> ItemTombstones { get; }

    // Catalogue (shared)
    DbSet<Work> Works { get; }

    DbSet<Release> Releases { get; }

    DbSet<ReleaseIdentifier> ReleaseIdentifiers { get; }

    DbSet<Asset> Assets { get; }

    DbSet<Platform> Platforms { get; }

    DbSet<ProviderCacheEntry> ProviderCache { get; }

    DbSet<ProviderQuota> ProviderQuotas { get; }

    // Scanning
    DbSet<ScanSession> ScanSessions { get; }

    DbSet<ScanSnap> ScanSnaps { get; }

    DbSet<ScanCandidate> ScanCandidates { get; }

    DbSet<ProvisionalRecord> ProvisionalRecords { get; }

    // Operations
    DbSet<Job> Jobs { get; }

    DbSet<MatchFeedback> MatchFeedback { get; }

    DbSet<AuditEvent> AuditEvents { get; }

    DbSet<IdempotencyKey> IdempotencyKeys { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
