namespace CollectionMaxxing.Application.Accounts;

/// <summary>Shows a new setup code to whoever runs the server (the log and <c>secrets/setup-code.txt</c>).</summary>
public interface ISetupCodeAnnouncer
{
    Task AnnounceAsync(string code, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
