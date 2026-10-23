using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>Use case: the identities the person can sign in with (<c>GET /api/v1/me/sign-in-methods</c>), oldest first.</summary>
public static class ListSignInMethods
{
    public sealed class Handler(IAppDbContext db)
    {
        /// <param name="userId">Whose sign-in methods.</param>
        /// <param name="currentLoginId">The identity behind this session, so that it can be marked as current.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<Page<SignInMethodDto>> HandleAsync(Guid userId, Guid? currentLoginId, CancellationToken cancellationToken)
        {
            var logins = await db.Logins.AsNoTracking()
                .Where(l => l.UserId == userId)
                .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
                .ToListAsync(cancellationToken);
            return new Page<SignInMethodDto>([.. logins.Select(l =>
                new SignInMethodDto(l.Id, ProviderOf(l.Subject), l.Email, l.CreatedAt, l.LastUsedAt, l.Id == currentLoginId))]);
        }
    }

    /// <summary>Which provider an Auth0 <c>sub</c> comes from: <c>google-oauth2|…</c> is Google, <c>apple|…</c> is Apple.</summary>
    public static string ProviderOf(string subject) =>
        subject.StartsWith("google-oauth2|", StringComparison.Ordinal) ? "google"
        : subject.StartsWith("apple|", StringComparison.Ordinal) ? "apple"
        : "other";
}

/// <summary>Use case: remove one of the person's sign-in methods, unless it is their last (they'd be locked out).</summary>
public static class RemoveSignInMethod
{
    public sealed class Handler(IAccountStore accounts)
    {
        /// <summary>Somebody else's identity, or one that doesn't exist, is the same "not found".</summary>
        public Task<Result> HandleAsync(Guid userId, Guid loginId, CancellationToken cancellationToken) =>
            accounts.UnlinkUnlessLastAsync(userId, loginId, cancellationToken);
    }
}
