using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Catalog;

// Concurrency (#292): catalogue rows deliberately carry no xmin token. Works are written once at creation and never
// updated afterwards; assets are immutable (init-only). A release's only post-creation write is cover assignment
// (ReleaseCovers points a coverless release at its fetched cover), and concurrent writers each store a valid cover,
// so last-write-wins is benign — a token would turn that race into a user-visible conflict on scan commit. Adding a
// token later needs a lead-owned migration (the model snapshot must match) and a revisit of ConcurrencyTests.

internal sealed class WorkConfiguration : IEntityTypeConfiguration<Work>
{
    public void Configure(EntityTypeBuilder<Work> builder)
    {
        builder.ToTable("works", t => t.HasCheckConstraint("ck_works_kind", EnumColumns.CheckIn<WorkKind>("kind")));
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever(); // UUIDv7 from IIdGenerator
        builder.Property(w => w.Kind).HasSnakeCaseEnum();
        builder.Property(w => w.Title).HasMaxLength(500).IsRequired();
        builder.Property(w => w.SortTitle).HasMaxLength(500).IsRequired();
        builder.Property(w => w.NormalizedTitle).HasMaxLength(500).IsRequired();
        builder.Property(w => w.CreatorsJson).HasColumnName("creators").HasColumnType("jsonb").IsRequired();
        builder.Property(w => w.Genres).HasColumnType("text[]").IsRequired();
        builder.Property(w => w.ExternalIdsJson).HasColumnName("external_ids").HasColumnType("jsonb").IsRequired();

        builder.HasOne<Asset>().WithMany().HasForeignKey(w => w.CoverAssetId).OnDelete(DeleteBehavior.Restrict);

        // Library and catalogue search: pg_trgm similarity on the normalised title.
        builder.HasIndex(w => w.NormalizedTitle)
            .HasDatabaseName("ix_works_normalized_title_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
        builder.HasIndex(w => new { w.Kind, w.SortTitle });
    }
}

internal sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.ToTable("releases", t =>
        {
            t.HasCheckConstraint("ck_releases_format", EnumColumns.CheckIn<ReleaseFormat>("format"));
            t.HasCheckConstraint("ck_releases_region", EnumColumns.CheckIn<ReleaseRegion>("region"));
            t.HasCheckConstraint("ck_releases_source", EnumColumns.CheckIn<ReleaseSource>("source"));
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Format).HasSnakeCaseEnum();
        builder.Property(r => r.Region).HasSnakeCaseEnum();
        builder.Property(r => r.Source).HasSnakeCaseEnum();
        builder.Property(r => r.Edition).HasMaxLength(200);
        builder.Property(r => r.Publisher).HasMaxLength(200);
        builder.Property(r => r.ProviderPayloadsJson).HasColumnName("provider_payloads").HasColumnType("jsonb");

        builder.HasOne<Work>().WithMany().HasForeignKey(r => r.WorkId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Platform>().WithMany().HasForeignKey(r => r.PlatformId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Asset>().WithMany().HasForeignKey(r => r.CoverAssetId).OnDelete(DeleteBehavior.Restrict);

        // A library's own releases go with the library.
        builder.HasOne<Library>().WithMany().HasForeignKey(r => r.OwnerLibraryId).OnDelete(DeleteBehavior.Cascade);

        // The commit that made a private release; the session going away just makes it an ordinary private release.
        builder.HasOne<ScanSession>().WithMany().HasForeignKey(r => r.CreatedBySessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(r => r.CreatedBySessionId).HasFilter("created_by_session_id is not null");

        builder.HasIndex(r => r.WorkId);
        builder.HasIndex(r => r.PlatformId);
        builder.HasIndex(r => r.OwnerLibraryId).HasFilter("owner_library_id is not null");
    }
}

internal sealed class ReleaseIdentifierConfiguration : IEntityTypeConfiguration<ReleaseIdentifier>
{
    public void Configure(EntityTypeBuilder<ReleaseIdentifier> builder)
    {
        builder.ToTable("release_identifiers", t =>
            t.HasCheckConstraint("ck_release_identifiers_type", EnumColumns.CheckIn<IdentifierType>("type")));

        // One barcode may belong to more than one release, but only once to each, and a library that learned it has its own
        // link beside the shared one. A nullable library cannot be part of a key, so the row has a key of its own and two
        // partial unique indexes say what must be unique: (type, value, release) among shared links, and the same with
        // the library among learned ones.
        // The database fills the id in and keeps doing so for good: a deploy that fails its smoke test rolls back to an image that
        // inserts (type, value, release_id) without one, and it must still be able to write links.
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(i => i.Type).HasSnakeCaseEnum();
        builder.Property(i => i.Value).HasMaxLength(64).IsRequired();

        builder.HasOne<Release>().WithMany().HasForeignKey(i => i.ReleaseId).OnDelete(DeleteBehavior.Cascade);

        // The commit that taught a link; the session going away just makes it an ordinary link, still the library's.
        builder.HasOne<ScanSession>().WithMany().HasForeignKey(i => i.LearnedBySessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(i => i.LearnedBySessionId).HasFilter("learned_by_session_id is not null");

        // The library that taught a link; what it taught goes with it.
        builder.HasOne<Library>().WithMany().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => i.LibraryId).HasFilter("library_id is not null");
        builder.HasIndex(i => new { i.Type, i.Value, i.ReleaseId }).HasDatabaseName("ux_release_identifiers_shared").IsUnique()
            .HasFilter("library_id is null");
        builder.HasIndex(i => new { i.Type, i.Value, i.ReleaseId, i.LibraryId }).HasDatabaseName("ux_release_identifiers_learned").IsUnique()
            .HasFilter("library_id is not null");

        // The barcode lookup.
        builder.HasIndex(i => new { i.Type, i.Value });
        builder.HasIndex(i => i.ReleaseId);
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets", t => t.HasCheckConstraint("ck_assets_kind", EnumColumns.CheckIn<AssetKind>("kind")));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Kind).HasSnakeCaseEnum();
        builder.Property(a => a.BlobPath).HasMaxLength(500).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.SourceUrl).HasMaxLength(2000);

        builder.HasOne<Library>().WithMany().HasForeignKey(a => a.OwnerLibraryId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.OwnerLibraryId, a.Sha256 });
    }
}

internal sealed class PlatformConfiguration : IEntityTypeConfiguration<Platform>
{
    public void Configure(EntityTypeBuilder<Platform> builder)
    {
        builder.ToTable("platforms");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.ShortName).HasMaxLength(30).IsRequired();
        builder.Property(p => p.Manufacturer).HasMaxLength(100);

        builder.HasIndex(p => p.Name).IsUnique();
        builder.HasIndex(p => p.IgdbId).IsUnique(); // many nulls are fine: nulls never collide
    }
}

internal sealed class ProviderCacheEntryConfiguration : IEntityTypeConfiguration<ProviderCacheEntry>
{
    public void Configure(EntityTypeBuilder<ProviderCacheEntry> builder)
    {
        builder.ToTable("provider_cache");
        builder.HasKey(c => new { c.Provider, c.Key });
        builder.Property(c => c.Provider).HasMaxLength(50);
        builder.Property(c => c.Key).HasMaxLength(500);
        builder.Property(c => c.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();

        // Expired rows are found and cleared by date.
        builder.HasIndex(c => c.ExpiresAt);
    }
}

internal sealed class ProviderQuotaConfiguration : IEntityTypeConfiguration<ProviderQuota>
{
    public void Configure(EntityTypeBuilder<ProviderQuota> builder)
    {
        builder.ToTable("provider_quota", t =>
        {
            t.HasCheckConstraint("ck_provider_quota_used", "used >= 0");
            t.HasCheckConstraint("ck_provider_quota_limit", "\"limit\" >= 0");
        });
        builder.HasKey(q => new { q.Provider, q.Day });
        builder.Property(q => q.Provider).HasMaxLength(50);
    }
}
