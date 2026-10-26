using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Scanning;

internal sealed class ProvisionalRecordConfiguration : IEntityTypeConfiguration<ProvisionalRecord>
{
    public void Configure(EntityTypeBuilder<ProvisionalRecord> builder)
    {
        builder.ToTable("provisional_records", t =>
        {
            t.HasCheckConstraint("ck_provisional_records_status", EnumColumns.CheckIn<ProvisionalStatus>("status"));
            t.HasCheckConstraint("ck_provisional_records_media_hint", EnumColumns.CheckIn<ScanMediaHint>("media_hint"));
            t.HasCheckConstraint("ck_provisional_records_resolve_action", EnumColumns.CheckIn<ProvisionalResolveAction>("resolve_action"));
            t.HasCheckConstraint(
                "ck_provisional_records_resolved",
                "(resolved_at is null and resolve_action is null) or (resolved_at is not null and resolve_action is not null)");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasAlternateKey(p => new { p.LibraryId, p.Id }).HasName("ak_provisional_records_library_id_id");
        builder.Property(p => p.Status).HasSnakeCaseEnum();
        builder.Property(p => p.MediaHint).HasSnakeCaseEnum();
        builder.Property(p => p.ResolveAction).HasSnakeCaseEnum();
        builder.Property(p => p.TitleGuess).HasMaxLength(500);

        builder.HasOne<Library>().WithMany().HasForeignKey(p => p.LibraryId).OnDelete(DeleteBehavior.Cascade);

        // The record outlives the session, snap and candidate it came from (design §11): whoever deletes those rows
        // clears these links first (ProvisionalLinks), because the record itself stays. Restrict, not SetNull: nulling a
        // composite would null library_id too, which must never be null.
        builder.HasOne<ScanSession>().WithMany()
            .HasForeignKey(p => new { p.LibraryId, p.SessionId })
            .HasPrincipalKey(s => new { s.LibraryId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ScanSnap>().WithMany()
            .HasForeignKey(p => new { p.LibraryId, p.SnapId })
            .HasPrincipalKey(s => new { s.LibraryId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ScanCandidate>().WithMany()
            .HasForeignKey(p => new { p.LibraryId, p.CandidateId })
            .HasPrincipalKey(c => new { c.LibraryId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Asset>().WithMany().HasForeignKey(p => p.CropAssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany()
            .HasForeignKey(p => new { p.LibraryId, p.LocationId })
            .HasPrincipalKey(l => new { l.LibraryId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The "Needs attention" list: what still needs acting on, stalest first.
        builder.HasIndex(p => new { p.LibraryId, p.ResolvedAt, p.CreatedAt });
    }
}
