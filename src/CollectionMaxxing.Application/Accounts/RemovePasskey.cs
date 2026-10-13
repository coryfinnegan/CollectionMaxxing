using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>Reads and removes a user's passkeys. Implemented by Infrastructure on ASP.NET Core Identity's passkey table.</summary>
public interface IPasskeyStore
{
    /// <summary>Removes the passkey only if the user keeps at least one other, in a single statement so two removals can't race. Returns whether it was removed.</summary>
    Task<bool> RemoveUnlessLastAsync(Guid userId, byte[] credentialId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid userId, byte[] credentialId, CancellationToken cancellationToken);
}

/// <summary>Use case: remove one of a user's passkeys, unless it is their last (they'd be locked out).</summary>
public static class RemovePasskey
{
    public sealed record Command(Guid UserId, string CredentialId);

    public sealed class Handler(IPasskeyStore passkeys)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (!TryDecode(command.CredentialId, out var credentialId))
            {
                return Result.Failure(AccountErrors.PasskeyNotFound);
            }

            if (await passkeys.RemoveUnlessLastAsync(command.UserId, credentialId, cancellationToken))
            {
                return Result.Success();
            }

            return Result.Failure(await passkeys.ExistsAsync(command.UserId, credentialId, cancellationToken)
                ? AccountErrors.LastPasskey
                : AccountErrors.PasskeyNotFound);
        }

        // Credential ids travel as base64url text.
        private static bool TryDecode(string text, out byte[] bytes)
        {
            try
            {
                var padded = text.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
                bytes = Convert.FromBase64String(padded);
                return bytes.Length > 0;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }
    }
}
