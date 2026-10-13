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

        // citext: "Garage" and "garage" are the same name, in the database as in the domain.
        builder.Property(l => l.Name).HasColumnType("citext").HasMaxLength(Location.MaxNameLength).IsRequired();

        builder.HasOne<Library>().WithMany().HasForeignKey(l => l.LibraryId).OnDelete(DeleteBehavior.Cascade);

        // (library, id) is what children point at, so a parent from another library is impossible in the database too.
        builder.HasAlternateKey(l => new { l.LibraryId, l.Id }).HasName("ak_locations_library_id_id");

        // The tree. A location with children can't be deleted by accident. A top-level location has no parent,
        // and a composite key with a null part isn't checked.
        builder.HasOne<Location>().WithMany()
            .HasForeignKey(l => new { l.LibraryId, l.ParentId })
            .HasPrincipalKey(l => new { l.LibraryId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Names are unique per parent. NULLS NOT DISTINCT makes two top-level names collide too.
        // The domain also checks this; the index is the backstop for races.
        builder.HasIndex(l => new { l.LibraryId, l.ParentId, l.Name })
            .HasDatabaseName("ux_locations_name_per_parent")
            .IsUnique()
            .AreNullsDistinct(false);

        builder.HasIndex(l => new { l.LibraryId, l.ParentId, l.SortOrder });
    }
}
