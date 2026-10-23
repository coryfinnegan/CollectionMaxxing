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

internal sealed class UserLoginConfiguration : IEntityTypeConfiguration<UserLogin>
{
    public void Configure(EntityTypeBuilder<UserLogin> builder)
    {
        builder.ToTable("user_logins", t => t.HasCheckConstraint("ck_user_logins_provider", "provider = 'auth0'"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Provider).HasMaxLength(32).IsRequired();
        builder.Property(l => l.Subject).HasMaxLength(UserLogin.MaxSubjectLength).IsRequired();
        builder.Property(l => l.Email).HasMaxLength(UserLogin.MaxEmailLength);

        // An identity belongs to one user at most: the unique index is what makes "link" and "join" safe when two finish at once.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(l => l.Subject).IsUnique();
        builder.HasIndex(l => l.UserId);
    }
}

internal sealed class AppSignInConfiguration : IEntityTypeConfiguration<AppSignIn>
{
    public void Configure(EntityTypeBuilder<AppSignIn> builder)
    {
        builder.ToTable("app_sign_ins");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.DeviceName).HasMaxLength(AppSignIn.MaxDeviceNameLength).IsRequired();

        // A phone registers once per install: signing in again on it updates this row. The unique index makes two registrations at once safe.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.UserId, s.InstallId }).IsUnique();
    }
}

internal sealed class WebSessionConfiguration : IEntityTypeConfiguration<WebSession>
{
    public void Configure(EntityTypeBuilder<WebSession> builder)
    {
        builder.ToTable("web_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Label).HasMaxLength(WebSession.MaxLabelLength).IsRequired();

        // A user's sessions go with the user. Lookups are by id (every cookie request) and by user (Settings, sign out everywhere).
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.UserId);
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

        // A recovery code is for one named account, and goes with it.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.UserId);
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
        builder.Property(i => i.CodeHash).HasMaxLength(200);

        builder.HasOne<Library>().WithMany().HasForeignKey(i => i.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AcceptedBy).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => i.CodeHash).IsUnique();
        builder.HasIndex(i => new { i.LibraryId, i.ExpiresAt });
    }
}
