using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>An identity Auth0 vouched for: its <c>sub</c>, what it says about the person, and when they last signed in at Auth0.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, DateTimeOffset? AuthTime);

/// <summary>What the browser set out to do when it went to Auth0.</summary>
public enum SignInPurpose
{
    /// <summary>Sign in. An identity nobody knows yet becomes a new account (ADR-026).</summary>
    SignIn = 1,

    /// <summary>Signed in already; prove it's still you (Auth0 with <c>max_age=0</c>).</summary>
    Reauth = 2,

    /// <summary>Somebody lost every sign-in method: link a new one to the account the recovery code names.</summary>
    Recovery = 4,

    /// <summary>Signed in with a recent sign-in: add another identity to yourself.</summary>
    Link = 6,
}

/// <summary>
/// What the server decided before the browser went to Auth0, and remembers (sealed, in a cookie) until it comes back. Each purpose
/// other than <see cref="SignInPurpose.SignIn"/> carries what allowed it: a recovery code, or the signed-in user.
/// </summary>
/// <param name="Purpose">What finishing the sign-in does.</param>
/// <param name="CodeId">The recovery code that was checked.</param>
/// <param name="UserId">Who was signed in when the browser left (reauth, link).</param>
/// <param name="SessionAuthTime">When that person last signed in (link needs it to be recent).</param>
public sealed record SignInIntent(
    SignInPurpose Purpose,
    Guid? CodeId = null,
    Guid? UserId = null,
    DateTimeOffset? SessionAuthTime = null);

public enum SignInOutcomeKind
{
    /// <summary>Start a session for <see cref="SignInOutcome.UserId"/>.</summary>
    SignedIn = 1,

    /// <summary>The identity was linked to the signed-in user. Their session stays as it is.</summary>
    Linked = 2,

    /// <summary>Nobody has this identity yet: it becomes a new account (<see cref="CreateAccount"/>). Nothing was created so far.</summary>
    NewIdentity = 3,

    /// <summary>Refused for a reason not worth telling (a deleted account, a spent code).</summary>
    Failed = 4,

    /// <summary>The identity already belongs to somebody, so it can't be linked to another account.</summary>
    AlreadyLinked = 7,

    /// <summary>Reauth or link without a recent sign-in, or by a different person.</summary>
    ReauthFailed = 8,
}

/// <summary>How a sign-in ended. <see cref="UserId"/> and <see cref="LoginId"/> are set for <see cref="SignInOutcomeKind.SignedIn"/> and <see cref="SignInOutcomeKind.Linked"/>.</summary>
public sealed record SignInOutcome(SignInOutcomeKind Kind, Guid? UserId = null, Guid? LoginId = null)
{
    public static readonly SignInOutcome NewIdentity = new(SignInOutcomeKind.NewIdentity);
    public static readonly SignInOutcome Failed = new(SignInOutcomeKind.Failed);
    public static readonly SignInOutcome AlreadyLinked = new(SignInOutcomeKind.AlreadyLinked);
    public static readonly SignInOutcome ReauthFailed = new(SignInOutcomeKind.ReauthFailed);
}

/// <summary>A <c>user_logins</c> row that was just written (or found).</summary>
public sealed record LinkedLogin(Guid LoginId, Guid UserId);

/// <summary>Writes accounts. Implemented by Infrastructure on ASP.NET Core Identity.</summary>
public interface IAccountStore
{
    /// <summary>
    /// In one transaction: a new user called <paramref name="displayName"/> (with the identity's email) and the <c>user_logins</c> row that
    /// links <paramref name="identity"/> to it. When the identity was linked meanwhile (two first sign-ins at once), the unique index on the
    /// subject decides: nothing of this call is kept and the winner's row is returned.
    /// </summary>
    Task<LinkedLogin> CreateAccountAsync(Guid userId, string displayName, ExternalIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: spends <paramref name="codeId"/> when there is one, and links <paramref name="identity"/> to the active user
    /// <paramref name="userId"/>. An identity already linked to that same user is fine; linked to anyone else, it fails with
    /// <see cref="AccountErrors.AlreadyLinked"/> and nothing changes.
    /// </summary>
    Task<Result<LinkedLogin>> LinkAsync(Guid userId, ExternalIdentity identity, Guid? codeId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one of the user's identities, but never the last: the user's row is locked while their identities are counted, so two
    /// removals at once can't take away both of the last two. Fails with <see cref="AccountErrors.LastSignInMethod"/> or
    /// <see cref="AccountErrors.SignInMethodNotFound"/> (somebody else's, or none).
    /// </summary>
    Task<Result> UnlinkUnlessLastAsync(Guid userId, Guid loginId, CancellationToken cancellationToken);
}

/// <summary>Use case: check a one-time recovery code.</summary>
public static class VerifyCode
{
    public sealed class Handler(IAppDbContext db, TimeProvider time)
    {
        /// <summary>Looks the code up. Unknown is a plain "invalid"; a spent or out-of-date code says so.</summary>
        public async Task<Result<Guid>> HandleAsync(string? code, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code) || OneTimeCodes.Normalize(code).Length == 0)
            {
                return AccountErrors.CodeRequired;
            }

            var hash = OneTimeCodes.Hash(code);
            var stored = await db.SetupCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == hash, cancellationToken);
            if (stored is null || stored.Purpose != SetupCodePurpose.Recovery)
            {
                return AccountErrors.CodeInvalid;
            }

            if (stored.UsedAt is not null)
            {
                return AccountErrors.CodeUsed;
            }

            return stored.ExpiresAt <= time.GetUtcNow() ? AccountErrors.CodeExpired : stored.Id;
        }
    }
}

/// <summary>
/// Use case: <c>POST /account/recover</c>. Checks the code before the browser goes to Auth0; the code is spent only when the sign-in comes
/// back (<see cref="CompleteSignIn"/>), in the same transaction that links the identity to the account the code names.
/// </summary>
public static class PlanSignIn
{
    public sealed class Handler(VerifyCode.Handler verify)
    {
        public async Task<Result<SignInIntent>> ForRecoveryAsync(string? code, CancellationToken cancellationToken)
        {
            var verified = await verify.HandleAsync(code, cancellationToken);
            return verified.IsSuccess ? new SignInIntent(SignInPurpose.Recovery, CodeId: verified.Value) : verified.Error!;
        }
    }
}

/// <summary>
/// Use case: Auth0 has vouched for an identity and the browser is back at <c>/signin-oidc</c>. Decide what that identity may do, given what
/// the browser set out to do (docs/15). A linked identity signs in; an unknown one is a new account (<see cref="SignInOutcome.NewIdentity"/>,
/// which the caller turns into one with <see cref="CreateAccount"/>), unless the browser came to link it or recover with it. People are never
/// matched by email.
/// </summary>
public static class CompleteSignIn
{
    /// <summary>How old the last sign-in may be for reauth and for linking another identity.</summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(5);

    /// <summary>An <c>auth_time</c> this far in the future is still believed (clocks differ a little).</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>True when <paramref name="authTime"/> is within <see cref="RecentSignIn"/> of <paramref name="now"/>.</summary>
    public static bool IsRecent(DateTimeOffset? authTime, DateTimeOffset now) =>
        authTime is { } at && now - at <= RecentSignIn && at - now <= ClockSkew;

    public sealed class Handler(IAppDbContext db, IUserDirectory users, IAccountStore accounts, TimeProvider time)
    {
        public async Task<SignInOutcome> HandleAsync(SignInIntent intent, ExternalIdentity identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(identity.Subject) || identity.Subject.Length > UserLogin.MaxSubjectLength)
            {
                return SignInOutcome.Failed;
            }

            var linked = await db.Logins.AsNoTracking()
                .Where(l => l.Subject == identity.Subject)
                .Select(l => new LinkedLogin(l.Id, l.UserId))
                .FirstOrDefaultAsync(cancellationToken);
            var now = time.GetUtcNow();

            switch (intent.Purpose)
            {
                case SignInPurpose.SignIn:
                    return linked is null ? SignInOutcome.NewIdentity : await SignInAsync(linked, identity, cancellationToken);

                case SignInPurpose.Reauth:
                    // Proving it's still you: the same person, and Auth0 says they signed in just now.
                    return linked is not null && linked.UserId == intent.UserId && IsRecent(identity.AuthTime, now)
                        ? await SignInAsync(linked, identity, cancellationToken)
                        : SignInOutcome.ReauthFailed;

                case SignInPurpose.Recovery:
                    return await RecoverAsync(intent, identity, linked, cancellationToken);

                case SignInPurpose.Link:
                    return await LinkAsync(intent, identity, linked, now, cancellationToken);

                default:
                    return SignInOutcome.Failed;
            }
        }

        private async Task<SignInOutcome> SignInAsync(LinkedLogin linked, ExternalIdentity identity, CancellationToken cancellationToken)
        {
            // A deleted account keeps its links until it is purged, so that nobody else can take them over, and signs in nobody.
            if (await users.IsDeactivatedAsync(linked.UserId, cancellationToken))
            {
                return SignInOutcome.Failed;
            }

            var now = time.GetUtcNow();
            var email = AccountText.Trim(identity.Email, UserLogin.MaxEmailLength);
            await db.Logins.Where(l => l.Id == linked.LoginId)
                .ExecuteUpdateAsync(set => set.SetProperty(l => l.LastUsedAt, now).SetProperty(l => l.Email, l => email ?? l.Email), cancellationToken);
            return new SignInOutcome(SignInOutcomeKind.SignedIn, linked.UserId, linked.LoginId);
        }

        private async Task<SignInOutcome> RecoverAsync(SignInIntent intent, ExternalIdentity identity, LinkedLogin? linked, CancellationToken cancellationToken)
        {
            // The code names the account. Recovery only ever adds an identity to that account: it never moves one away from somebody
            // else, and never makes a new account.
            var account = intent.CodeId is { } codeId
                ? await db.SetupCodes.AsNoTracking().Where(c => c.Id == codeId).Select(c => (Guid?)c.UserId).FirstOrDefaultAsync(cancellationToken)
                : null;
            if (account is not { } userId)
            {
                return SignInOutcome.Failed;
            }

            if (linked is not null && linked.UserId != userId)
            {
                return SignInOutcome.AlreadyLinked;
            }

            var result = await accounts.LinkAsync(userId, identity, intent.CodeId, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.Error == AccountErrors.AlreadyLinked ? SignInOutcome.AlreadyLinked : SignInOutcome.Failed;
            }

            return await SignInAsync(result.Value, identity, cancellationToken);
        }

        private async Task<SignInOutcome> LinkAsync(SignInIntent intent, ExternalIdentity identity, LinkedLogin? linked, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (intent.UserId is not { } userId || !IsRecent(intent.SessionAuthTime, now))
            {
                return SignInOutcome.ReauthFailed;
            }

            if (linked is not null)
            {
                return linked.UserId == userId ? new SignInOutcome(SignInOutcomeKind.Linked, userId, linked.LoginId) : SignInOutcome.AlreadyLinked;
            }

            var result = await accounts.LinkAsync(userId, identity, null, cancellationToken);
            return result.IsSuccess
                ? new SignInOutcome(SignInOutcomeKind.Linked, userId, result.Value.LoginId)
                : result.Error == AccountErrors.AlreadyLinked ? SignInOutcome.AlreadyLinked : SignInOutcome.Failed;
        }
    }
}

/// <summary>
/// Use case: the first sign-in of an identity nobody has yet makes it an account (ADR-026): on the web from the ID token, on the iPhone from
/// Auth0's <c>/userinfo</c>. Never from what the caller says about itself, and never matched to anyone by email.
/// </summary>
public static class CreateAccount
{
    /// <summary>The display name when Auth0 gives none.</summary>
    public const string FallbackName = "Collector";

    public sealed class Handler(IAccountStore accounts, IUserDirectory users, IIdGenerator ids)
    {
        /// <summary>
        /// Creates the account, or finds the one a sign-in at the same moment created. Fails (and the caller signs nobody in) only when that
        /// account was deleted meanwhile.
        /// </summary>
        public async Task<Result<LinkedLogin>> HandleAsync(ExternalIdentity identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(identity.Subject) || identity.Subject.Length > UserLogin.MaxSubjectLength)
            {
                return AccountErrors.NoAccount;
            }

            var name = AccountText.Trim(identity.Name, AccountText.MaxDisplayNameLength) ?? FallbackName;
            var login = await accounts.CreateAccountAsync(ids.NewId(), name, identity, cancellationToken);
            return await users.IsDeactivatedAsync(login.UserId, cancellationToken) ? AccountErrors.NoAccount : login;
        }
    }
}

/// <summary>Tidying what Auth0 says about a person before it is stored.</summary>
public static class AccountText
{
    /// <summary>The same as <c>AppUser.MaxDisplayNameLength</c>.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>Trimmed and cut to <paramref name="maxLength"/>; null when blank.</summary>
    public static string? Trim(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
