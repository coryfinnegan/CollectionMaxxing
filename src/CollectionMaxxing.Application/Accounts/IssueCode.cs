using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>A one-time code in the clear. This is the only moment it exists in the clear: the database keeps a hash.</summary>
public sealed record IssuedCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Use case: <c>cm-admin recovery-code &lt;email&gt;</c>, a support tool for the person at the host (docs/15, "Recovery"). Makes a
/// recovery code for the one account with that email. The sign-in it later links goes to that account only.
/// </summary>
public static class IssueCode
{
    public sealed class Handler(IAppDbContext db, IUserDirectory users, IIdGenerator ids, TimeProvider time)
    {
        /// <summary>
        /// Finds the account (its own email or the email of one of its sign-ins, ignoring case) and makes a new code for it, cancelling any
        /// older unused one of that account. An unknown email, or one that two accounts share, is an error and nothing is issued.
        /// </summary>
        public async Task<Result<IssuedCode>> HandleAsync(string? email, CancellationToken cancellationToken)
        {
            var address = email?.Trim();
            if (string.IsNullOrEmpty(address))
            {
                return AccountErrors.RecoveryEmailRequired;
            }

            var accounts = await users.FindActiveByEmailAsync(address, cancellationToken);
            if (accounts.Count == 0)
            {
                return AccountErrors.RecoveryEmailUnknown;
            }

            if (accounts.Count > 1)
            {
                return AccountErrors.RecoveryEmailAmbiguous;
            }

            var userId = accounts[0];
            await db.SetupCodes
                .Where(c => c.UserId == userId && c.UsedAt == null)
                .ExecuteDeleteAsync(cancellationToken);

            var now = time.GetUtcNow();
            var code = OneTimeCodes.Generate();
            var expiresAt = now + OneTimeCodes.Lifetime;
            db.SetupCodes.Add(new SetupCode
            {
                Id = ids.NewId(),
                Purpose = SetupCodePurpose.Recovery,
                UserId = userId,
                CodeHash = OneTimeCodes.Hash(code),
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync(cancellationToken);
            return new IssuedCode(code, expiresAt);
        }
    }
}
