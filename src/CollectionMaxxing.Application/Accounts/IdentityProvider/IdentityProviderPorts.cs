using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts.IdentityProvider;

/// <summary>
/// What the server asks of the identity provider (Auth0's Management API, ADR-025). Every call can be repeated: an identity that is already
/// gone counts as done. A call that fails throws <see cref="IdentityProviderException"/>, and the caller decides whether to retry later.
/// </summary>
public interface IIdentityProviderAdmin
{
    /// <summary>Ends every refresh token of this identity, so every phone signed in with it must sign in again. (Auth0: its grants.)</summary>
    Task RevokeRefreshTokensAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Deletes the identity at the provider, so nothing is left there.</summary>
    Task DeleteUserAsync(string subject, CancellationToken cancellationToken);
}

/// <summary>The identity provider refused or could not be reached. The message never holds a token or a secret.</summary>
public sealed class IdentityProviderException : Exception
{
    public IdentityProviderException(string message)
        : base(message)
    {
    }

    public IdentityProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The moment before which the server refuses a person's access tokens (<c>users.tokens_not_before</c>). Access tokens live up to an hour
/// after their refresh tokens are gone, and this closes that gap. A token is refused only if it was issued strictly before the moment,
/// compared in whole seconds (a token's <c>iat</c> has no fraction), so a sign-in made right after a revoke always works.
/// </summary>
public interface ITokenCutoffs
{
    /// <summary>Refuses every token issued before <paramref name="at"/> from now on. A later cutoff wins; an earlier one never lowers it.</summary>
    Task RaiseAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The cutoff, or null when none was ever set.</summary>
    Task<DateTimeOffset?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>The rule that compares a token's <c>iat</c> with a cutoff.</summary>
public static class TokenCutoff
{
    /// <summary>
    /// True when the token must be refused: there is a cutoff and the token was issued strictly before its second (or says nothing about when).
    /// A token issued in the same second as the cutoff is let through, so a sign-in right after a revoke works.
    /// </summary>
    public static bool Refuses(DateTimeOffset? cutoff, long? issuedAtSeconds) =>
        cutoff is { } at && (issuedAtSeconds is not { } issued || issued < at.ToUnixTimeSeconds());
}

public sealed record RevokeIdentitySessionsPayload(Guid UserId);

/// <summary>
/// Job: the retry of "end this person's Auth0 refresh tokens", queued when the Management API failed while a member was removed, an account
/// deleted or the phones signed out. The person is already locked out by the token cutoff; this finishes the job at Auth0. It fails (and is
/// retried with backoff) while Auth0 keeps refusing. Safe to repeat.
/// </summary>
public static class RevokeIdentitySessions
{
    public const string JobType = "identity.revoke-sessions";

    public static string DedupeKey(Guid userId) => $"{JobType}:{userId:N}";

    public sealed class Handler(IAppDbContext db, IIdentityProviderAdmin admin) : IJobHandler<RevokeIdentitySessionsPayload>
    {
        public async Task HandleAsync(RevokeIdentitySessionsPayload payload, CancellationToken cancellationToken)
        {
            var subjects = await db.Logins.AsNoTracking().Where(l => l.UserId == payload.UserId).Select(l => l.Subject).ToListAsync(cancellationToken);
            foreach (var subject in subjects)
            {
                await admin.RevokeRefreshTokensAsync(subject, cancellationToken);
            }
        }
    }
}

/// <summary>
/// Part of the purge (30 days after an account was deleted): deletes the person's Auth0 users, so nothing is left at Auth0. It runs before the
/// linked identities are erased, which is how it knows who to delete; a second run finds none and does nothing. A failure fails the purge, which retries.
/// </summary>
public sealed class DeleteIdentityProviderUsers(IAppDbContext db, IIdentityProviderAdmin admin) : IUserPurgeStep
{
    public async Task PurgeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var subjects = await db.Logins.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.Subject).ToListAsync(cancellationToken);
        foreach (var subject in subjects)
        {
            await admin.DeleteUserAsync(subject, cancellationToken);
        }
    }
}
