using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Identity;

internal sealed class PasskeyStore(AppDbContext db) : IPasskeyStore
{
    public async Task<bool> RemoveUnlessLastAsync(Guid userId, byte[] credentialId, CancellationToken cancellationToken)
    {
        // One statement: the passkey goes only if the user has more than one, counted at the moment of the delete.
        var removed = await db.UserPasskeys
            .Where(p => p.UserId == userId
                && p.CredentialId == credentialId
                && db.UserPasskeys.Count(other => other.UserId == userId) > 1)
            .ExecuteDeleteAsync(cancellationToken);
        return removed == 1;
    }

    public Task<bool> ExistsAsync(Guid userId, byte[] credentialId, CancellationToken cancellationToken) =>
        db.UserPasskeys.AnyAsync(p => p.UserId == userId && p.CredentialId == credentialId, cancellationToken);
}
