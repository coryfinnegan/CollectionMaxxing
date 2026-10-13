using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CollectionMaxxing.Infrastructure.Identity;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users");
        builder.Property(u => u.DisplayName).HasMaxLength(AppUser.MaxDisplayNameLength).IsRequired();

        // Identity's own index names are PascalCase; keep every index snake_case.
        builder.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ux_users_normalized_user_name").IsUnique();
        builder.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
    }
}

internal sealed class DevicePairingConfiguration : IEntityTypeConfiguration<DevicePairing>
{
    public void Configure(EntityTypeBuilder<DevicePairing> builder)
    {
        builder.ToTable("device_pairings", t =>
        {
            t.HasCheckConstraint("ck_device_pairings_attempts", "attempts >= 0");
            t.HasCheckConstraint("ck_device_pairings_expiry", "expires_at > created_at");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.TokenHash).HasMaxLength(200).IsRequired();
        builder.Property(p => p.CodeHash).HasMaxLength(200).IsRequired();
        builder.Property(p => p.DeviceName).HasMaxLength(200);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);

        // Redeeming looks the pairing up by hash. A hash identifies exactly one pairing.
        builder.HasIndex(p => p.TokenHash).IsUnique();
        builder.HasIndex(p => new { p.UserId, p.ExpiresAt });
    }
}

internal sealed class SetupCodeConfiguration : IEntityTypeConfiguration<SetupCode>
{
    public void Configure(EntityTypeBuilder<SetupCode> builder)
    {
        builder.ToTable("setup_codes", t =>
            t.HasCheckConstraint("ck_setup_codes_purpose", EnumColumns.CheckIn<SetupCodePurpose>("purpose")));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Purpose).HasSnakeCaseEnum();
        builder.Property(c => c.CodeHash).HasMaxLength(200).IsRequired();

        builder.HasIndex(c => c.CodeHash).IsUnique();
    }
}

internal sealed class LibraryInviteConfiguration : IEntityTypeConfiguration<LibraryInvite>
{
    public void Configure(EntityTypeBuilder<LibraryInvite> builder)
    {
        builder.ToTable("library_invites", t =>
        {
            // An invite can never make an owner: there is exactly one, and ownership moves by a separate action.
            t.HasCheckConstraint("ck_library_invites_role", "role in ('editor', 'viewer')");
            t.HasCheckConstraint("ck_library_invites_expiry", "expires_at > created_at");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Role)
            .HasConversion(role => role.ToString().ToLowerInvariant(), text => Enum.Parse<LibraryRole>(text, true))
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(i => i.Name).HasMaxLength(LibraryInvite.MaxNameLength).IsRequired();
        builder.Property(i => i.TokenHash).HasMaxLength(200).IsRequired();

        builder.HasOne<Library>().WithMany().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AcceptedBy).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => new { i.LibraryId, i.ExpiresAt });
    }
}
