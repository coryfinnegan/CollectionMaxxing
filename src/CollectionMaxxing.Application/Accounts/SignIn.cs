using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>An identity Auth0 vouched for: its <c>sub</c>, what it says about the person (display only), and when they last signed in at Auth0.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, DateTimeOffset? AuthTime);

/// <summary>What the browser set out to do when it went to Auth0. Only <see cref="SignIn"/> needs nothing besides a linked identity.</summary>
public enum SignInPurpose
{
    /// <summary>Sign in with an identity that is already linked.</summary>
    SignIn = 1,

    /// <summary>Signed in already; prove it's still you (Auth0 with <c>max_age=0</c>).</summary>
    Reauth = 2,

    /// <summary>First run: the identity becomes the owner. Backed by the setup code, only while there is no owner.</summary>
    Setup = 3,

    /// <summary>The owner lost every identity: link a new one to the owner. Backed by a recovery code.</summary>
    Recovery = 4,

    /// <summary>Join a library. Backed by the invite token.</summary>
    Invite = 5,

    /// <summary>Signed in with a recent sign-in: add another identity to yourself.</summary>
    Link = 6,
}

/// <summary>
/// What the server decided before the browser went to Auth0, and remembers (sealed, in a cookie) until it comes back. Each purpose
/// other than <see cref="SignInPurpose.SignIn"/> carries what allowed it: a code, an invite token, or the signed-in user.
/// </summary>
/// <param name="Purpose">What finishing the sign-in does.</param>
/// <param name="CodeId">The setup or recovery code that was checked.</param>
/// <param name="InviteToken">The invite token from the link.</param>
/// <param name="UserId">Who was signed in when the browser left (reauth, link).</param>
/// <param name="SessionAuthTime">When that person last signed in (link needs it to be recent).</param>
public sealed record SignInIntent(
    SignInPurpose Purpose,
    Guid? CodeId = null,
    string? InviteToken = null,
    Guid? UserId = null,
    DateTimeOffset? SessionAuthTime = null);

public enum SignInOutcomeKind
{
    /// <summary>Start a session for <see cref="SignInOutcome.UserId"/>.</summary>
    SignedIn = 1,

    /// <summary>The identity was linked to the signed-in user. Their session stays as it is.</summary>
    Linked = 2,

    /// <summary>Nobody is linked to this identity and nothing allowed linking it. Nothing was created.</summary>
    NotLinked = 3,

    /// <summary>Refused for a reason not worth telling (a deactivated account, a spent code, no owner to recover).</summary>
    Failed = 4,

    InviteInvalid = 5,

    SetupClosed = 6,

    /// <summary>The identity already belongs to somebody, so it can't be linked or join again.</summary>
    AlreadyLinked = 7,

    /// <summary>Reauth or link without a recent sign-in, or by a different person.</summary>
    ReauthFailed = 8,
}

/// <summary>How a sign-in ended. <see cref="UserId"/> and <see cref="LoginId"/> are set for <see cref="SignInOutcomeKind.SignedIn"/> and <see cref="SignInOutcomeKind.Linked"/>.</summary>
public sealed record SignInOutcome(SignInOutcomeKind Kind, Guid? UserId = null, Guid? LoginId = null)
{
    public static readonly SignInOutcome NotLinked = new(SignInOutcomeKind.NotLinked);
    public static readonly SignInOutcome Failed = new(SignInOutcomeKind.Failed);
    public static readonly SignInOutcome InviteInvalid = new(SignInOutcomeKind.InviteInvalid);
    public static readonly SignInOutcome SetupClosed = new(SignInOutcomeKind.SetupClosed);
    public static readonly SignInOutcome AlreadyLinked = new(SignInOutcomeKind.AlreadyLinked);
    public static readonly SignInOutcome ReauthFailed = new(SignInOutcomeKind.ReauthFailed);
}

/// <summary>A <c>user_logins</c> row that was just written.</summary>
public sealed record LinkedLogin(Guid LoginId, Guid UserId);

/// <summary>Writes accounts. Implemented by Infrastructure on ASP.NET Core Identity.</summary>
public interface IAccountStore
{
    /// <summary>
    /// In one transaction: spends the setup code, creates the owner linked to <paramref name="identity"/>, their personal library and the
    /// platform admin role. Fails (and changes nothing) if the code was used or expired meanwhile, an owner exists, or the identity is linked.
    /// </summary>
    Task<Result<LinkedLogin>> CreateOwnerAsync(Guid userId, string displayName, ExternalIdentity identity, Guid setupCodeId, CancellationToken cancellationToken);

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

/// <summary>Use case: is the first-run setup still open? It is until the first owner exists, and then never again.</summary>
public static class GetSetupStatus
{
    public sealed class Handler(IAppDbContext db)
    {
        public async Task<bool> IsOpenAsync(CancellationToken cancellationToken) =>
            !await IssueCode.OwnerExistsAsync(db, cancellationToken);
    }
}

/// <summary>Use case: check a one-time code.</summary>
public static class VerifyCode
{
    public sealed class Handler(IAppDbContext db, TimeProvider time)
    {
        /// <summary>
        /// Looks the code up. Unknown, or made for another purpose, is a plain "invalid" (a recovery code doesn't reveal that it
        /// is one); a spent or out-of-date code says so.
        /// </summary>
        public async Task<Result<Guid>> HandleAsync(SetupCodePurpose purpose, string? code, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code) || OneTimeCodes.Normalize(code).Length == 0)
            {
                return AccountErrors.CodeRequired;
            }

            var hash = OneTimeCodes.Hash(code);
            var stored = await db.SetupCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == hash, cancellationToken);
            if (stored is null || stored.Purpose != purpose)
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
/// Use case: <c>POST /setup</c> and <c>POST /account/recover</c>. Checks the code before the browser goes to Auth0; the code is spent only
/// when the sign-in comes back (<see cref="CompleteSignIn"/>), in the same transaction that links the identity.
/// </summary>
public static class PlanSignIn
{
    public sealed class Handler(IAppDbContext db, VerifyCode.Handler verify)
    {
        /// <summary>First run. Fails with a 404-style error once an owner exists, whatever the code.</summary>
        public async Task<Result<SignInIntent>> ForSetupAsync(string? code, CancellationToken cancellationToken)
        {
            if (await IssueCode.OwnerExistsAsync(db, cancellationToken))
            {
                return AccountErrors.SetupClosed;
            }

            var verified = await verify.HandleAsync(SetupCodePurpose.Setup, code, cancellationToken);
            return verified.IsSuccess ? new SignInIntent(SignInPurpose.Setup, CodeId: verified.Value) : verified.Error!;
        }

        /// <summary>The owner lost every identity: the recovery code lets them link a new one.</summary>
        public async Task<Result<SignInIntent>> ForRecoveryAsync(string? code, CancellationToken cancellationToken)
        {
            var verified = await verify.HandleAsync(SetupCodePurpose.Recovery, code, cancellationToken);
            if (!verified.IsSuccess)
            {
                return verified.Error!;
            }

            return await CompleteSignIn.FindOwnerAsync(db, cancellationToken) is null
                ? AccountErrors.NoOwner
                : new SignInIntent(SignInPurpose.Recovery, CodeId: verified.Value);
        }
    }
}

/// <summary>
/// Use case: Auth0 has vouched for an identity and the browser is back at <c>/signin-oidc</c>. Decide what that identity may do, given
/// what the browser set out to do (docs/14, "Linking Auth0 identities to our users"). An identity that is linked signs in; one that isn't
/// gets in only through setup, an invite, linking while signed in, or a recovery code. People are never matched by email.
/// </summary>
public static class CompleteSignIn
{
    /// <summary>How old the last sign-in may be for reauth and for linking another identity.</summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(5);

    /// <summary>An <c>auth_time</c> this far in the future is still believed (clocks differ a little).</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private const int MaxDisplayNameLength = 100;

    /// <summary>True when <paramref name="authTime"/> is within <see cref="RecentSignIn"/> of <paramref name="now"/>.</summary>
    public static bool IsRecent(DateTimeOffset? authTime, DateTimeOffset now) =>
        authTime is { } at && now - at <= RecentSignIn && at - now <= ClockSkew;

    public sealed class Handler(
        IAppDbContext db,
        IUserDirectory users,
        IAccountStore accounts,
        IInviteAcceptance invites,
        ISetupCodeAnnouncer announcer,
        IIdGenerator ids,
        TimeProvider time)
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
                    return linked is null ? SignInOutcome.NotLinked : await SignInAsync(linked, identity, cancellationToken);

                case SignInPurpose.Reauth:
                    // Proving it's still you: the same person, and Auth0 says they signed in just now.
                    return linked is not null && linked.UserId == intent.UserId && IsRecent(identity.AuthTime, now)
                        ? await SignInAsync(linked, identity, cancellationToken)
                        : SignInOutcome.ReauthFailed;

                case SignInPurpose.Setup:
                    return await SetUpAsync(intent, identity, linked, cancellationToken);

                case SignInPurpose.Recovery:
                    return await RecoverAsync(intent, identity, linked, cancellationToken);

                case SignInPurpose.Invite:
                    return await JoinAsync(intent, identity, linked, cancellationToken);

                case SignInPurpose.Link:
                    return await LinkAsync(intent, identity, linked, now, cancellationToken);

                default:
                    return SignInOutcome.Failed;
            }
        }

        private async Task<SignInOutcome> SignInAsync(LinkedLogin linked, ExternalIdentity identity, CancellationToken cancellationToken)
        {
            // A removed member or a deleted account keeps its links, so that nobody else can take them over, and signs in nobody.
            if (await users.IsDeactivatedAsync(linked.UserId, cancellationToken))
            {
                return SignInOutcome.Failed;
            }

            var now = time.GetUtcNow();
            var email = Trim(identity.Email, UserLogin.MaxEmailLength);
            await db.Logins.Where(l => l.Id == linked.LoginId)
                .ExecuteUpdateAsync(set => set.SetProperty(l => l.LastUsedAt, now).SetProperty(l => l.Email, l => email ?? l.Email), cancellationToken);
            return new SignInOutcome(SignInOutcomeKind.SignedIn, linked.UserId, linked.LoginId);
        }

        private async Task<SignInOutcome> SetUpAsync(SignInIntent intent, ExternalIdentity identity, LinkedLogin? linked, CancellationToken cancellationToken)
        {
            if (intent.CodeId is not { } codeId || await IssueCode.OwnerExistsAsync(db, cancellationToken))
            {
                return SignInOutcome.SetupClosed;
            }

            if (linked is not null)
            {
                return SignInOutcome.AlreadyLinked;
            }

            var name = Trim(identity.Name, MaxDisplayNameLength) ?? "Owner";
            var created = await accounts.CreateOwnerAsync(ids.NewId(), name, identity, codeId, cancellationToken);
            if (!created.IsSuccess)
            {
                return created.Error == AccountErrors.SetupClosed ? SignInOutcome.SetupClosed
                    : created.Error == AccountErrors.AlreadyLinked ? SignInOutcome.AlreadyLinked
                    : SignInOutcome.Failed;
            }

            // The setup code is spent for good, so the copy left in secrets/setup-code.txt goes.
            await announcer.WithdrawAsync(cancellationToken);
            return new SignInOutcome(SignInOutcomeKind.SignedIn, created.Value.UserId, created.Value.LoginId);
        }

        private async Task<SignInOutcome> RecoverAsync(SignInIntent intent, ExternalIdentity identity, LinkedLogin? linked, CancellationToken cancellationToken)
        {
            var owner = await FindOwnerAsync(db, cancellationToken);
            if (intent.CodeId is not { } codeId || owner is null)
            {
                return SignInOutcome.Failed;
            }

            // Recovery only ever adds an identity to the owner: it never moves one away from somebody else, and never makes a new user.
            if (linked is not null && linked.UserId != owner)
            {
                return SignInOutcome.AlreadyLinked;
            }

            var result = await accounts.LinkAsync(owner.Value, identity, codeId, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.Error == AccountErrors.AlreadyLinked ? SignInOutcome.AlreadyLinked : SignInOutcome.Failed;
            }

            return await SignInAsync(result.Value, identity, cancellationToken);
        }

        private async Task<SignInOutcome> JoinAsync(SignInIntent intent, ExternalIdentity identity, LinkedLogin? linked, CancellationToken cancellationToken)
        {
            // Someone who already has an account doesn't get a second one: they sign in as themselves.
            if (linked is not null)
            {
                return SignInOutcome.AlreadyLinked;
            }

            var invite = await PlanInvite.FindUsableAsync(db, intent.InviteToken, time.GetUtcNow(), cancellationToken);
            if (invite is null)
            {
                return SignInOutcome.InviteInvalid;
            }

            var accepted = await invites.AcceptAsync(invite.Id, ids.NewId(), invite.Name, identity, cancellationToken);
            if (!accepted.IsSuccess)
            {
                return accepted.Error == AccountErrors.AlreadyLinked ? SignInOutcome.AlreadyLinked : SignInOutcome.InviteInvalid;
            }

            return new SignInOutcome(SignInOutcomeKind.SignedIn, accepted.Value.UserId, accepted.Value.LoginId);
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

        private static string? Trim(string? text, int maxLength)
        {
            var trimmed = text?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
        }
    }

    /// <summary>The owner of the server (the one owner there is), or null. Reads memberships across libraries.</summary>
    internal static async Task<Guid?> FindOwnerAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var owner = await db.LibraryMembers.IgnoreQueryFilters()
            .Where(m => m.Role == LibraryRole.Owner)
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        return owner == Guid.Empty ? null : owner;
    }
}
