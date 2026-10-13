using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Identity;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Operations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs", t =>
        {
            t.HasCheckConstraint("ck_jobs_status", EnumColumns.CheckIn<JobStatus>("status"));
            t.HasCheckConstraint("ck_jobs_attempts", "attempts >= 0");
        });
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Type).HasMaxLength(100).IsRequired();
        builder.Property(j => j.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.Status).HasSnakeCaseEnum();
        builder.Property(j => j.LockedBy).HasMaxLength(200);

        // The worker polls "pending and due" with FOR UPDATE SKIP LOCKED; this keeps that scan cheap.
        builder.HasIndex(j => new { j.Status, j.RunAfter });
    }
}

internal sealed class MatchFeedbackConfiguration : IEntityTypeConfiguration<MatchFeedback>
{
    public void Configure(EntityTypeBuilder<MatchFeedback> builder)
    {
        builder.ToTable("match_feedback");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.HasOne<Library>().WithMany().HasForeignKey(f => f.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ScanCandidate>().WithMany()
            .HasForeignKey(f => new { f.LibraryId, f.CandidateId })
            .HasPrincipalKey(c => new { c.LibraryId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.CandidateId);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(100);
        builder.Property(e => e.IpHash).HasMaxLength(100);
        builder.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");

        // When an account is purged its audit entries stay, anonymised. library_id has no foreign key on purpose:
        // the trail outlives the library.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.At);
        builder.HasIndex(e => new { e.LibraryId, e.At }).IsDescending(false, true);
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
    }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(k => new { k.UserId, k.Key });
        builder.Property(k => k.Key).HasMaxLength(200);
        builder.Property(k => k.RequestHash).HasMaxLength(100).IsRequired();
        builder.Property(k => k.ResponseBodyJson).HasColumnName("response_body").HasColumnType("jsonb");

        builder.HasOne<AppUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);

        // Expired keys are found and deleted by date.
        builder.HasIndex(k => k.ExpiresAt);
    }
}
