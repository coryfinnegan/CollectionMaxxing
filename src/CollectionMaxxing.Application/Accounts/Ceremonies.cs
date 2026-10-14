using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>Why a passkey is being made. Registration is allowed only for one of these, each backed by something the server checked.</summary>
public enum CeremonyPurpose
{
    /// <summary>First run: creates the owner. Backed by the setup code.</summary>
    Setup,

    /// <summary>The owner lost every passkey. Backed by a recovery code.</summary>
    Recovery,

    /// <summary>A signed-in user adds another passkey. Backed by a recent passkey sign-in.</summary>
    AddPasskey,

    /// <summary>A family member joins by invite. Backed by the invite token (built by ID-3).</summary>
    Invite,
}

/// <summary>
/// What the server decided when it handed out passkey creation options: who the passkey is for and what allowed it.
/// It travels in a protected cookie between "creation options" and "register", so register can never run on its own.
/// </summary>
public sealed record CeremonyPlan(CeremonyPurpose Purpose, Guid UserId, string DisplayName, Guid? CodeId);

/// <summary>A passkey as the browser made it, in the terms the application understands.</summary>
public sealed record PasskeyCredential(
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignCount,
    IReadOnlyList<string> Transports,
    bool IsUserVerified,
    bool IsBackupEligible,
    bool IsBackedUp,
    byte[] AttestationObject,
    byte[] ClientDataJson,
    string? Name,
    DateTimeOffset CreatedAt);

/// <summary>Writes accounts. Implemented by Infrastructure on ASP.NET Core Identity.</summary>
public interface IAccountStore
{
    /// <summary>
    /// In one transaction: spends the setup code, creates the owner with their passkey, their personal library and the
    /// platform admin role. Fails (and changes nothing) if the code was used or expired meanwhile, or an owner exists.
    /// </summary>
    Task<Result<SignedInDto>> CreateOwnerAsync(Guid userId, string displayName, PasskeyCredential passkey, Guid setupCodeId, CancellationToken cancellationToken);

    /// <summary>Adds a passkey to an existing user, and spends <paramref name="codeId"/> in the same transaction when there is one.</summary>
    Task<Result<SignedInDto>> AddPasskeyAsync(Guid userId, PasskeyCredential passkey, Guid? codeId, CancellationToken cancellationToken);
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

/// <summary>Use case: decide who a passkey about to be made is for, before the browser is asked to make it.</summary>
public static class PlanCeremony
{
    public sealed class Handler(IAppDbContext db, VerifyCode.Handler verify, IIdGenerator ids)
    {
        /// <summary>First run. Fails with a 404-style error once an owner exists, whatever the code.</summary>
        public async Task<Result<CeremonyPlan>> ForSetupAsync(string? code, string? displayName, CancellationToken cancellationToken)
        {
            if (await IssueCode.OwnerExistsAsync(db, cancellationToken))
            {
                return AccountErrors.SetupClosed;
            }

            var name = CleanName(displayName);
            if (name.Error is { } nameError)
            {
                return nameError;
            }

            var verified = await verify.HandleAsync(SetupCodePurpose.Setup, code, cancellationToken);
            return verified.IsSuccess
                ? new CeremonyPlan(CeremonyPurpose.Setup, ids.NewId(), name.Value, verified.Value)
                : verified.Error!;
        }

        /// <summary>The owner lost their passkeys: the recovery code lets them register a new one.</summary>
        public async Task<Result<CeremonyPlan>> ForRecoveryAsync(string? code, CancellationToken cancellationToken)
        {
            var verified = await verify.HandleAsync(SetupCodePurpose.Recovery, code, cancellationToken);
            if (!verified.IsSuccess)
            {
                return verified.Error!;
            }

            var owner = await db.LibraryMembers.IgnoreQueryFilters()
                .Where(m => m.Role == LibraryRole.Owner)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            return owner == Guid.Empty
                ? AccountErrors.NoOwner
                : new CeremonyPlan(CeremonyPurpose.Recovery, owner, string.Empty, verified.Value);
        }

        private static Result<string> CleanName(string? displayName)
        {
            var name = displayName?.Trim() ?? string.Empty;
            return name.Length == 0
                ? AccountErrors.DisplayNameRequired
                : name.Length > 100 ? AccountErrors.DisplayNameTooLong : name;
        }
    }
}

/// <summary>Use case: the browser made the passkey. Finish what the plan allowed.</summary>
public static class CompleteCeremony
{
    public sealed class Handler(IAccountStore accounts, ISetupCodeAnnouncer announcer)
    {
        public async Task<Result<SignedInDto>> HandleAsync(CeremonyPlan plan, PasskeyCredential passkey, CancellationToken cancellationToken)
        {
            switch (plan.Purpose)
            {
                case CeremonyPurpose.Setup:
                    var created = await accounts.CreateOwnerAsync(plan.UserId, plan.DisplayName, passkey, plan.CodeId!.Value, cancellationToken);
                    if (created.IsSuccess)
                    {
                        // The setup code is spent for good, so the copy left in secrets/setup-code.txt goes.
                        await announcer.WithdrawAsync(cancellationToken);
                    }

                    return created;
                case CeremonyPurpose.Recovery:
                    return await accounts.AddPasskeyAsync(plan.UserId, passkey, plan.CodeId, cancellationToken);
                case CeremonyPurpose.AddPasskey:
                    return await accounts.AddPasskeyAsync(plan.UserId, passkey, null, cancellationToken);
                default:
                    throw new ArgumentOutOfRangeException(nameof(plan), plan.Purpose, "This kind of ceremony is built by ID-3.");
            }
        }
    }
}
