using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>Use case: remove one of a user's passkeys, unless it is their last.</summary>
public static class RemovePasskey
{
    public sealed record Command(Guid UserId, string CredentialId);

    public sealed class Handler
    {
        public Task<Result> HandleAsync(Command command, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
