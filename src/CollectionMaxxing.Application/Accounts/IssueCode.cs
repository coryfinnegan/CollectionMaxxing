using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>A one-time code in the clear. This is the only moment it exists in the clear: the database keeps a hash.</summary>
public sealed record IssuedCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>Use case: make a one-time setup or recovery code.</summary>
public static class IssueCode
{
    public sealed class Handler
    {
        public Task<IssuedCode?> HandleAsync(SetupCodePurpose purpose, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
