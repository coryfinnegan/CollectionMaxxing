using CollectionMaxxing.Application.Accounts.IdentityProvider;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>An iPhone that is signed in, as Settings shows it.</summary>
public sealed record AppSignInDto(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

/// <summary>Why registering a phone was refused.</summary>
public static class AppSignInErrors
{
    public static readonly DomainError InstallIdInvalid =
        new(ErrorKind.Validation, "app_sign_in.install_id_invalid", "Send the app's install id, a UUID, in the X-Install-Id header.", "X-Install-Id");

    public static readonly DomainError DeviceNameInvalid =
        new(ErrorKind.Validation, "app_sign_in.device_name_invalid", $"Give the phone a name of 1 to {AppSignIn.MaxDeviceNameLength} characters.", "deviceName");
}

/// <summary>
/// The phones of a person, on <c>app_sign_ins</c>, and the way to sign them all out. This is the <see cref="IDeviceAuthorizations"/> that member
/// removal and account deletion already call. Signing out means: raise the person's token cutoff (so access tokens issued before now are
/// refused at once), forget the phones, and ask the identity provider to end their refresh tokens. If the provider fails, nothing else fails:
/// the cutoff already locks the phones out and <see cref="RevokeIdentitySessions"/> finishes the job later.
/// </summary>
public sealed class AppSignInDevices(
    IAppDbContext db,
    ITokenCutoffs cutoffs,
    IIdentityProviderAdmin identityProvider,
    IJobQueue jobs,
    TimeProvider clock,
    ILogger<AppSignInDevices> logger) : IDeviceAuthorizations
{
    public async Task<IReadOnlyList<DeviceSession>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.AppSignIns.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new DeviceSession(s.Id, s.DeviceName, s.CreatedAt, s.LastSeenAt, false))
            .ToListAsync(cancellationToken);

    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        // First, and in our own database: from here on every access token issued before now is refused, whatever happens at Auth0.
        await cutoffs.RaiseAsync(userId, clock.GetUtcNow(), cancellationToken);
        await db.AppSignIns.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        var subjects = await db.Logins.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.Subject).ToListAsync(cancellationToken);
        try
        {
            foreach (var subject in subjects)
            {
                await identityProvider.RevokeRefreshTokensAsync(subject, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the kind of failure is logged: never the subject, a token or the provider's answer.
            logger.LogWarning("Could not end a person's refresh tokens at the identity provider ({ExceptionType}); a retry is queued.", exception.GetType().Name);
            await jobs.EnqueueAsync(
                RevokeIdentitySessions.JobType,
                new RevokeIdentitySessionsPayload(userId),
                new JobEnqueueOptions(null, RevokeIdentitySessions.DedupeKey(userId)),
                cancellationToken);
        }
    }
}

/// <summary>Use case: the app tells the server about this phone after it signs in (and again on later launches).</summary>
public static class RegisterAppSignIn
{
    public sealed record Command(Guid UserId, Guid InstallId, string? DeviceName);

    /// <param name="SignIn">The phone as stored.</param>
    /// <param name="Created">True when this was the first time this install registered (HTTP 201), false for an update (200).</param>
    public sealed record Registered(AppSignInDto SignIn, bool Created);

    public sealed class Handler(IAppDbContext db, TimeProvider clock)
    {
        public async Task<Result<Registered>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var name = command.DeviceName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > AppSignIn.MaxDeviceNameLength)
            {
                return Result<Registered>.Failure(AppSignInErrors.DeviceNameInvalid);
            }

            var now = clock.GetUtcNow();
            var created = false;
            if (await UpdateAsync(command, name, now, cancellationToken) == 0)
            {
                db.AppSignIns.Add(new AppSignIn
                {
                    Id = Guid.CreateVersion7(),
                    UserId = command.UserId,
                    InstallId = command.InstallId,
                    DeviceName = name,
                    CreatedAt = now,
                    LastSeenAt = now,
                });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    created = true;
                }
                catch (DbUpdateException)
                {
                    // Two registrations of the same install at once: the other one won the insert. This one is an update of it.
                    if (await UpdateAsync(command, name, now, cancellationToken) == 0)
                    {
                        throw;
                    }
                }
            }

            var row = await db.AppSignIns.AsNoTracking()
                .Where(s => s.UserId == command.UserId && s.InstallId == command.InstallId)
                .Select(s => new AppSignInDto(s.Id, s.DeviceName, s.CreatedAt, s.LastSeenAt))
                .SingleAsync(cancellationToken);
            return Result<Registered>.Success(new Registered(row, created));
        }

        private Task<int> UpdateAsync(Command command, string name, DateTimeOffset now, CancellationToken cancellationToken) =>
            db.AppSignIns
                .Where(s => s.UserId == command.UserId && s.InstallId == command.InstallId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.DeviceName, name).SetProperty(s => s.LastSeenAt, now), cancellationToken);
    }
}

/// <summary>Use case: the iPhones the person is signed in on.</summary>
public static class ListAppSignIns
{
    public sealed class Handler(IDeviceAuthorizations devices)
    {
        public async Task<Page<AppSignInDto>> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
            new([.. (await devices.ListAsync(userId, cancellationToken)).Select(d => new AppSignInDto(d.Id, d.DeviceName, d.CreatedAt, d.LastSeenAt))]);
    }
}

/// <summary>Use case: sign out every iPhone of the person. Auth0 can't end one phone's tokens on the Free plan, so it is all of them.</summary>
public static class SignOutAppSignIns
{
    public sealed class Handler(IDeviceAuthorizations devices)
    {
        public Task HandleAsync(Guid userId, CancellationToken cancellationToken) => devices.RevokeAllAsync(userId, cancellationToken);
    }
}
