using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>A one-time code in the clear. This is the only moment it exists in the clear: the database keeps a hash.</summary>
public sealed record IssuedCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>Use case: make a one-time setup or recovery code.</summary>
public static class IssueCode
{
    public sealed class Handler(IAppDbContext db, IIdGenerator ids, TimeProvider time)
    {
        /// <summary>
        /// Makes a new code and cancels any older unused one of the same kind, so only the code that was printed last works.
        /// A setup code can't be made once an owner exists: it returns null.
        /// </summary>
        public async Task<IssuedCode?> HandleAsync(SetupCodePurpose purpose, CancellationToken cancellationToken)
        {
            if (purpose == SetupCodePurpose.Setup && await OwnerExistsAsync(db, cancellationToken))
            {
                return null;
            }

            await db.SetupCodes
                .Where(c => c.Purpose == purpose && c.UsedAt == null)
                .ExecuteDeleteAsync(cancellationToken);

            var now = time.GetUtcNow();
            var code = OneTimeCodes.Generate();
            var expiresAt = now + OneTimeCodes.LifetimeOf(purpose);
            db.SetupCodes.Add(new SetupCode
            {
                Id = ids.NewId(),
                Purpose = purpose,
                CodeHash = OneTimeCodes.Hash(code),
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync(cancellationToken);
            return new IssuedCode(code, expiresAt);
        }
    }

    /// <summary>True once the first owner exists. Reads memberships across libraries, so it ignores the library filter.</summary>
    public static Task<bool> OwnerExistsAsync(IAppDbContext db, CancellationToken cancellationToken) =>
        db.LibraryMembers.IgnoreQueryFilters().AnyAsync(m => m.Role == LibraryRole.Owner, cancellationToken);
}
