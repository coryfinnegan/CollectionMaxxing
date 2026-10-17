using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Identity;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Scanning;

internal sealed class ScanSessionConfiguration : IEntityTypeConfiguration<ScanSession>
{
    public void Configure(EntityTypeBuilder<ScanSession> builder)
    {
        builder.ToTable("scan_sessions", t =>
        {
            t.HasCheckConstraint("ck_scan_sessions_status", EnumColumns.CheckIn<ScanSessionStatus>("status"));
            t.HasCheckConstraint("ck_scan_sessions_default_status", EnumColumns.CheckIn<ItemStatus>("default_status"));
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.LibraryId, s.Id }).HasName("ak_scan_sessions_library_id_id");
        builder.Property(s => s.Status).HasSnakeCaseEnum();
        builder.Property(s => s.DefaultStatus).HasSnakeCaseEnum();

        builder.HasOne<Library>().WithMany().HasForeignKey(s => s.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany()
            .HasForeignKey(s => new { s.LibraryId, s.DefaultLocationId })
            .HasPrincipalKey(l => new { l.LibraryId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.LibraryId, s.Status, s.CreatedAt });

        // Finds sessions to abandon after 7 days.
        builder.HasIndex(s => s.CreatedAt).HasFilter("status = 'open'");
    }
}

internal sealed class ScanSnapConfiguration : IEntityTypeConfiguration<ScanSnap>
{
    public void Configure(EntityTypeBuilder<ScanSnap> builder)
    {
        builder.ToTable("scan_snaps", t =>
        {
            t.HasCheckConstraint("ck_scan_snaps_trigger", EnumColumns.CheckIn<SnapTrigger>("trigger"));
            t.HasCheckConstraint("ck_scan_snaps_engine", EnumColumns.CheckIn<ScanEngine>("engine"));
            t.HasCheckConstraint("ck_scan_snaps_fallback_reason", EnumColumns.CheckIn<FallbackReason>("fallback_reason"));
            t.HasCheckConstraint("ck_scan_snaps_fell_back_has_reason", "fell_back = (fallback_reason is not null)");
            t.HasCheckConstraint("ck_scan_snaps_item_count", "item_count >= 0");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.SessionId, s.Id }).HasName("ak_scan_snaps_session_id_id");
        builder.Property(s => s.Trigger).HasSnakeCaseEnum();
        builder.Property(s => s.Engine).HasSnakeCaseEnum();
        builder.Property(s => s.FallbackReason).HasSnakeCaseEnum();

        builder.HasOne<Library>().WithMany().HasForeignKey(s => s.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ScanSession>().WithMany()
            .HasForeignKey(s => new { s.LibraryId, s.SessionId })
            .HasPrincipalKey(s => new { s.LibraryId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Asset>().WithMany().HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.SessionId);
    }
}

internal sealed class ScanCandidateConfiguration : IEntityTypeConfiguration<ScanCandidate>
{
    public void Configure(EntityTypeBuilder<ScanCandidate> builder)
    {
        builder.ToTable("scan_candidates", t =>
        {
            t.HasCheckConstraint("ck_scan_candidates_source", EnumColumns.CheckIn<CandidateSource>("source"));
            t.HasCheckConstraint("ck_scan_candidates_box_source", EnumColumns.CheckIn<BoxSource>("box_source"));
            t.HasCheckConstraint("ck_scan_candidates_resolution", EnumColumns.CheckIn<CandidateResolution>("resolution"));
            t.HasCheckConstraint("ck_scan_candidates_decision", EnumColumns.CheckIn<CandidateDecision>("decision"));
            t.HasCheckConstraint("ck_scan_candidates_edited_format", EnumColumns.CheckIn<ReleaseFormat>("edited_format"));
            t.HasCheckConstraint("ck_scan_candidates_copies", "copies >= 1");
            t.HasCheckConstraint("ck_scan_candidates_already_owned_count", "already_owned_count >= 0");
            t.HasCheckConstraint("ck_scan_candidates_score", "score between 0 and 1");
            t.HasCheckConstraint("ck_scan_candidates_art_distance", "art_distance >= 0");

            // The box is four numbers between 0 and 1, all present or all absent.
            t.HasCheckConstraint(
                "ck_scan_candidates_box",
                "(box_x is null and box_y is null and box_w is null and box_h is null)"
                + " or (box_x is not null and box_y is not null and box_w is not null and box_h is not null"
                + " and box_x between 0 and 1 and box_y between 0 and 1 and box_w between 0 and 1 and box_h between 0 and 1)");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.LibraryId, c.Id }).HasName("ak_scan_candidates_library_id_id");
        builder.Property(c => c.Source).HasSnakeCaseEnum();
        builder.Property(c => c.BoxSource).HasSnakeCaseEnum();
        builder.Property(c => c.Resolution).HasSnakeCaseEnum();
        builder.Property(c => c.Decision).HasSnakeCaseEnum();
        builder.Property(c => c.EditedFormat).HasSnakeCaseEnum();
        builder.Property(c => c.EditedPlatform).HasMaxLength(100);
        builder.Property(c => c.EditedEdition).HasMaxLength(100);

        // Set by the database (the trigger in the SCAN-1d migration), never by the app: see ScanCandidate.ChangeSeq.
        builder.Property(c => c.ChangeSeq).ValueGeneratedOnAddOrUpdate();
        builder.HasIndex(c => new { c.SessionId, c.ChangeSeq });
        builder.Property(c => c.RawCode).HasMaxLength(64);
        builder.Property(c => c.Symbology).HasMaxLength(32);
        builder.Property(c => c.VisibleText).HasMaxLength(2000);
        builder.Property(c => c.LocalId).HasMaxLength(100);
        builder.Property(c => c.IdempotencyKey).HasMaxLength(200);
        builder.Property(c => c.InferredJson).HasColumnName("inferred").HasColumnType("jsonb");
        builder.Property(c => c.AlternativesJson).HasColumnName("alternatives").HasColumnType("jsonb");
        builder.Property(c => c.TraceJson).HasColumnName("trace").HasColumnType("jsonb");

        builder.HasOne<Library>().WithMany().HasForeignKey(c => c.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ScanSession>().WithMany()
            .HasForeignKey(c => new { c.LibraryId, c.SessionId })
            .HasPrincipalKey(s => new { s.LibraryId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The snap belongs to the same session as the candidate that cites it.
        builder.HasOne<ScanSnap>().WithMany()
            .HasForeignKey(c => new { c.SessionId, c.SnapId })
            .HasPrincipalKey(s => new { s.SessionId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Release>().WithMany().HasForeignKey(c => c.MatchedReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Item>().WithMany()
            .HasForeignKey(c => new { c.LibraryId, c.CreatedItemId })
            .HasPrincipalKey(i => new { i.LibraryId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.SessionId, c.Seq }).IsUnique();

        // A retried request carries the same key and must not create a second candidate. Several rows may have no key.
        builder.HasIndex(c => new { c.SessionId, c.IdempotencyKey })
            .HasDatabaseName("ux_scan_candidates_idempotency")
            .IsUnique()
            .HasFilter("idempotency_key is not null");
    }
}
