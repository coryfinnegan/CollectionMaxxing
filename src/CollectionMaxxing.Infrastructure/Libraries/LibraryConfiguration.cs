using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Libraries;

internal sealed class LibraryConfiguration : IEntityTypeConfiguration<Library>
{
    public void Configure(EntityTypeBuilder<Library> builder)
    {
        builder.ToTable("libraries");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever(); // UUIDv7 from IIdGenerator
        builder.Property(l => l.Name).HasMaxLength(Library.MaxNameLength).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();
    }
}

internal sealed class LibraryMemberConfiguration : IEntityTypeConfiguration<LibraryMember>
{
    public void Configure(EntityTypeBuilder<LibraryMember> builder)
    {
        builder.ToTable("library_members");
        builder.HasKey(m => new { m.LibraryId, m.UserId });

        // Stored as 'owner' / 'editor' / 'viewer', matching docs/03.
        builder.Property(m => m.Role)
            .HasConversion(role => role.ToString().ToLowerInvariant(), text => Enum.Parse<LibraryRole>(text, true))
            .HasMaxLength(16)
            .IsRequired();

        // Exactly one owner per library.
        builder.HasIndex(m => m.LibraryId)
            .HasDatabaseName("ux_library_members_one_owner")
            .HasFilter("role = 'owner'")
            .IsUnique();

        builder.HasOne<Library>().WithMany().HasForeignKey(m => m.LibraryId).OnDelete(DeleteBehavior.Cascade);
    }
}
