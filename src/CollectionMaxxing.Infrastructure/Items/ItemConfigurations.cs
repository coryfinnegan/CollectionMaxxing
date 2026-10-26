using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Infrastructure.Identity;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Items;

internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("items", t =>
        {
            t.HasCheckConstraint("ck_items_status", EnumColumns.CheckIn<ItemStatus>("status"));
            t.HasCheckConstraint("ck_items_condition", EnumColumns.CheckIn<ItemCondition>("condition"));
            t.HasCheckConstraint("ck_items_completeness", EnumColumns.CheckIn<ItemCompleteness>("completeness"));
            t.HasCheckConstraint("ck_items_cover_choice", EnumColumns.CheckIn<CoverChoice>("cover_choice"));
            t.HasCheckConstraint("ck_items_added_via", EnumColumns.CheckIn<AddedVia>("added_via"));
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        // The pair (library, id) is what other library-scoped tables point at, so a child can't name an item of another library.
        builder.HasAlternateKey(i => new { i.LibraryId, i.Id }).HasName("ak_items_library_id_id");

        builder.Property(i => i.Status).HasSnakeCaseEnum();
        builder.Property(i => i.Condition).HasSnakeCaseEnum();
        builder.Property(i => i.Completeness).HasSnakeCaseEnum();
        builder.Property(i => i.CoverChoice).HasSnakeCaseEnum();
        builder.Property(i => i.AddedVia).HasSnakeCaseEnum();
        builder.Property(i => i.Notes).HasMaxLength(Item.MaxNotesLength);
        builder.Property(i => i.EditionVerified).HasDefaultValue(true);
        builder.Property(i => i.LentTo).HasMaxLength(200);
        builder.Property(i => i.CustomTitle).HasMaxLength(500);

        builder.HasOne<Library>().WithMany().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Release>().WithMany().HasForeignKey(i => i.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Asset>().WithMany().HasForeignKey(i => i.CustomCoverAssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.Restrict);

        // A location from another library is impossible: the pair must exist in locations. A location that still holds items can't be deleted.
        builder.HasOne<Location>().WithMany()
            .HasForeignKey(i => new { i.LibraryId, i.LocationId })
            .HasPrincipalKey(l => new { l.LibraryId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.LibraryId, i.Status });
        builder.HasIndex(i => new { i.LibraryId, i.EditionVerified });
        builder.HasIndex(i => new { i.LibraryId, i.ReleaseId });
        builder.HasIndex(i => new { i.LibraryId, i.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(i => new { i.LibraryId, i.LocationId });
        builder.HasIndex(i => i.CustomTitle).HasDatabaseName("ix_items_custom_title_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasAlternateKey(t => new { t.LibraryId, t.Id }).HasName("ak_tags_library_id_id");

        // citext: "Sci-Fi" and "sci-fi" are the same tag.
        builder.Property(t => t.Name).HasColumnType("citext").HasMaxLength(Tag.MaxNameLength).IsRequired();

        builder.HasOne<Library>().WithMany().HasForeignKey(t => t.LibraryId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.LibraryId, t.Name }).HasDatabaseName("ux_tags_name_per_library").IsUnique();
    }
}

internal sealed class ItemTagConfiguration : IEntityTypeConfiguration<ItemTag>
{
    public void Configure(EntityTypeBuilder<ItemTag> builder)
    {
        builder.ToTable("item_tags");
        builder.HasKey(t => new { t.ItemId, t.TagId });

        builder.HasOne<Library>().WithMany().HasForeignKey(t => t.LibraryId).OnDelete(DeleteBehavior.Cascade);

        // Item and tag must be in the same library as the row itself.
        builder.HasOne<Item>().WithMany()
            .HasForeignKey(t => new { t.LibraryId, t.ItemId })
            .HasPrincipalKey(i => new { i.LibraryId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tag>().WithMany()
            .HasForeignKey(t => new { t.LibraryId, t.TagId })
            .HasPrincipalKey(t => new { t.LibraryId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.LibraryId, t.TagId });
    }
}

internal sealed class ItemTombstoneConfiguration : IEntityTypeConfiguration<ItemTombstone>
{
    public void Configure(EntityTypeBuilder<ItemTombstone> builder)
    {
        builder.ToTable("item_tombstones");
        builder.HasKey(t => t.ItemId);
        builder.Property(t => t.ItemId).ValueGeneratedNever();

        builder.HasOne<Library>().WithMany().HasForeignKey(t => t.LibraryId).OnDelete(DeleteBehavior.Cascade);

        // What the ownership snapshot reads by (the same "time, then id" order as items), and what the daily clean-up trims.
        builder.HasIndex(t => new { t.LibraryId, t.DeletedAt, t.ItemId });
    }
}

internal sealed class ItemFeaturePrintConfiguration : IEntityTypeConfiguration<ItemFeaturePrint>
{
    public void Configure(EntityTypeBuilder<ItemFeaturePrint> builder)
    {
        builder.ToTable("item_feature_prints");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Data).HasColumnType("bytea").IsRequired();

        builder.HasOne<Library>().WithMany().HasForeignKey(p => p.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Item>().WithMany()
            .HasForeignKey(p => new { p.LibraryId, p.ItemId })
            .HasPrincipalKey(i => new { i.LibraryId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Release>().WithMany().HasForeignKey(p => p.ReleaseId).OnDelete(DeleteBehavior.Restrict);

        // One print per item, release and Apple revision; also what "since" syncs read by.
        builder.HasIndex(p => new { p.ItemId, p.ReleaseId, p.Revision }).IsUnique();
        builder.HasIndex(p => new { p.LibraryId, p.CreatedAt });
    }
}
