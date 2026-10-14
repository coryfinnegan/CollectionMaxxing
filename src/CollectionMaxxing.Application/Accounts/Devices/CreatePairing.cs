using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>Use case: the signed-in user (with a recent passkey) asks for a pairing, to put a phone on their account.</summary>
public static class CreatePairing
{
    public sealed class Handler(IAppDbContext db, IIdGenerator ids, TimeProvider time, IPublicServerUrl server, IDeviceAuthorizations devices)
    {
        public async Task<Result<PairDeviceDto>> HandleAsync(Guid userId, CancellationToken cancellationToken)
        {
            // A session can outlive its account for a moment (deleted from another device): no pairing for a user who isn't there.
            if (!await devices.UserIsActiveAsync(userId, cancellationToken))
            {
                return PairingErrors.UserGone;
            }

            var now = time.GetUtcNow();

            // Pairings nobody used are only clutter once their two minutes are up.
            await db.DevicePairings
                .Where(p => p.UserId == userId && p.UsedAt == null && p.ExpiresAt <= now)
                .ExecuteDeleteAsync(cancellationToken);

            var token = PairingSecrets.NewToken();
            var code = await NewUnusedCodeAsync(now, cancellationToken);
            var expiresAt = now + PairingSecrets.Lifetime;
            db.DevicePairings.Add(new DevicePairing
            {
                Id = ids.NewId(),
                UserId = userId,
                TokenHash = PairingSecrets.Hash(token),
                CodeHash = PairingSecrets.Hash(code),
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync(cancellationToken);
            return new PairDeviceDto(server.Value, token, code, expiresAt);
        }

        /// <summary>A code no other open pairing has, so typing it can only mean one pairing.</summary>
        private async Task<string> NewUnusedCodeAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var code = PairingSecrets.NewCode();
                var hash = PairingSecrets.Hash(code);
                if (!await db.DevicePairings.AnyAsync(p => p.CodeHash == hash && p.UsedAt == null && p.ExpiresAt > now, cancellationToken))
                {
                    return code;
                }
            }

            throw new InvalidOperationException("Could not find a free pairing code.");
        }
    }
}
