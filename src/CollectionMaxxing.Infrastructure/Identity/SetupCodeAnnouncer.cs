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

    public Task WithdrawAsync(CancellationToken cancellationToken)
    {
        // The code is spent, so the file is only clutter (and a code that looks valid to anyone who finds it).
        // The owner already exists by now, so a file that can't be deleted must not undo that: it is only clutter.
        var path = Path.Combine(options.Value.SecretsPath, FileName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCouldNotDelete(path, ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup is done, but {Path} could not be deleted ({Reason}). Delete it by hand: the code in it is spent.")]
    private partial void LogCouldNotDelete(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FIRST RUN: open the web app and enter the setup code {Code} (valid until {Expires}, single use). It is also in {Path}.")]
    private partial void LogSetupCode(string code, string expires, string path);
}
