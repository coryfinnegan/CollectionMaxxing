using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Locations;

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("locations");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever(); // UUIDv7 from IIdGenerator
        builder.Property(l => l.Name).HasMaxLength(Location.MaxNameLength).IsRequired();

        builder.HasOne<Library>().WithMany().HasForeignKey(l => l.LibraryId).OnDelete(DeleteBehavior.Cascade);

        // The tree. A location with children can't be deleted by accident.
        builder.HasOne<Location>().WithMany().HasForeignKey(l => l.ParentId).OnDelete(DeleteBehavior.Restrict);

        // Names are unique per parent. NULLS NOT DISTINCT makes two top-level names collide too.
        // The domain also checks this (case-insensitively); the index is the backstop for races.
        builder.HasIndex(l => new { l.LibraryId, l.ParentId, l.Name })
            .HasDatabaseName("ux_locations_name_per_parent")
            .IsUnique()
            .AreNullsDistinct(false);

        builder.HasIndex(l => new { l.LibraryId, l.ParentId, l.SortOrder });
    }
}
