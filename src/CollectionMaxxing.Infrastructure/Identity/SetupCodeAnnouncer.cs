using System.Globalization;
using CollectionMaxxing.Application.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>
/// Shows a new setup code to the person at the PC, the one place a code is ever printed: in the server log and in
/// <c>secrets/setup-code.txt</c> (git-ignored). Nothing else logs a code.
/// </summary>
public sealed partial class SetupCodeAnnouncer(IOptions<AuthOptions> options, ILogger<SetupCodeAnnouncer> logger) : ISetupCodeAnnouncer
{
    public const string FileName = "setup-code.txt";

    public async Task AnnounceAsync(string code, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var directory = options.Value.SecretsPath;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var expires = expiresAt.ToString("u", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(path, $"{code}{Environment.NewLine}Valid until {expires}. Single use.{Environment.NewLine}", cancellationToken);

        LogSetupCode(code, expires, path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FIRST RUN: open the web app and enter the setup code {Code} (valid until {Expires}, single use). It is also in {Path}.")]
    private partial void LogSetupCode(string code, string expires, string path);
}
