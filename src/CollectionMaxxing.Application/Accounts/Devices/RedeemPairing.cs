using System.Linq.Expressions;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>Use case: a phone turns a pairing (the QR token, or the 6-digit code) into a device of the user who made it.</summary>
public static class RedeemPairing
{
    public const int MaxDeviceNameLength = 100;
    public const string DefaultDeviceName = "Unnamed device";

    /// <summary>What the phone sent. Either <paramref name="Token"/> or <paramref name="Code"/> (or both, which must belong together).</summary>
    public sealed record Command(string? Token, string? Code, string? DeviceName);

    /// <summary>Whose device it is now, and the authorization its tokens hang from.</summary>
    public sealed record Redeemed(Guid UserId, Guid AuthorizationId);

    public sealed class Handler(IAppDbContext db, TimeProvider time, IDeviceAuthorizations devices)
    {
        public async Task<Result<Redeemed>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var token = string.IsNullOrWhiteSpace(command.Token) ? null : command.Token.Trim();
            var codeGiven = !string.IsNullOrWhiteSpace(command.Code);
            if (token is null && !codeGiven)
            {
                return PairingErrors.Missing;
            }

            // A code that isn't even six digits can't match, but still counts as a wrong guess.
            var codeHash = codeGiven ? PairingSecrets.Hash(PairingSecrets.NormalizeCode(command.Code) ?? "not-a-code") : null;
            var now = time.GetUtcNow();

            var pairing = token is not null
                ? await ClaimByTokenAsync(PairingSecrets.Hash(token), codeHash, now, cancellationToken)
                : await ClaimByCodeAsync(codeHash!, now, cancellationToken);
            if (pairing is null)
            {
                return PairingErrors.Invalid;
            }

            var authorizationId = await devices.CreateAsync(pairing.UserId, cancellationToken);
            if (authorizationId is null)
            {
                return PairingErrors.Invalid;
            }

            var name = CleanName(command.DeviceName);
            await db.DevicePairings
                .Where(p => p.Id == pairing.Id)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(p => p.AuthorizationId, authorizationId).SetProperty(p => p.DeviceName, name),
                    cancellationToken);
            return new Redeemed(pairing.UserId, authorizationId.Value);
        }

        /// <summary>
        /// Uses the pairing up, but only if it is still unused, in date and not locked: one statement, so two phones redeeming
        /// the same pairing at the same moment can't both win. Returns who it was for, or null when it failed.
        /// </summary>
        private async Task<Claimed?> ClaimByTokenAsync(string tokenHash, string? codeHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var claimed = await db.DevicePairings
                .Where(p => p.TokenHash == tokenHash
                    && p.UsedAt == null && p.ExpiresAt > now && p.Attempts < PairingSecrets.MaxWrongGuesses
                    && (codeHash == null || p.CodeHash == codeHash))
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.UsedAt, now), cancellationToken);
            if (claimed == 1)
            {
                return await db.DevicePairings.AsNoTracking()
                    .Where(p => p.TokenHash == tokenHash)
                    .Select(p => new Claimed(p.Id, p.UserId))
                    .SingleAsync(cancellationToken);
            }

            if (codeHash is not null)
            {
                // The token was right but the code on top of it wasn't: that is a wrong guess at this pairing.
                await CountWrongGuessAsync(p => p.TokenHash == tokenHash, now, cancellationToken);
            }

            return null;
        }

        private async Task<Claimed?> ClaimByCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var claimed = await db.DevicePairings
                .Where(p => p.CodeHash == codeHash
                    && p.UsedAt == null && p.ExpiresAt > now && p.Attempts < PairingSecrets.MaxWrongGuesses)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.UsedAt, now), cancellationToken);
            if (claimed == 1)
            {
                return await db.DevicePairings.AsNoTracking()
                    .Where(p => p.CodeHash == codeHash && p.UsedAt == now)
                    .Select(p => new Claimed(p.Id, p.UserId))
                    .SingleAsync(cancellationToken);
            }

            // Nobody can say which pairing a stranger's wrong code was aimed at, so it counts against every open one:
            // five wrong guesses in two minutes lock them all, which is what keeps a million-value code safe.
            await CountWrongGuessAsync(_ => true, now, cancellationToken);
            return null;
        }

        private async Task CountWrongGuessAsync(Expression<Func<DevicePairing, bool>> which, DateTimeOffset now, CancellationToken cancellationToken) =>
            await db.DevicePairings
                .Where(which)
                .Where(p => p.UsedAt == null && p.ExpiresAt > now && p.Attempts < PairingSecrets.MaxWrongGuesses)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Attempts, p => p.Attempts + 1), cancellationToken);

        private static string CleanName(string? name)
        {
            var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > MaxDeviceNameLength)
            {
                clean = clean[..MaxDeviceNameLength];
            }

            return clean.Length == 0 ? DefaultDeviceName : clean;
        }

        private sealed record Claimed(Guid Id, Guid UserId);
    }
}
