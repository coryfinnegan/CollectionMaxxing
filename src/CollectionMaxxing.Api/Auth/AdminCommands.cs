using System.Globalization;
using CollectionMaxxing.Application.Accounts;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Support commands for the person at the host, run as arguments of the API itself (docs/07, "Recovery"; docs/15):
/// <c>dotnet run --project src/CollectionMaxxing.Api -- --recovery-code someone@example.com</c>, or <c>cm-admin recovery-code
/// someone@example.com</c> in the container. Access to the host is the recovery factor; it isn't an app role. A command prints its result
/// and the program exits without serving.
/// </summary>
public static class AdminCommands
{
    public const string RecoveryCodeFlag = "--recovery-code";

    /// <summary>Runs the command named in <paramref name="args"/>, if any. Returns true when it did, so the caller can exit.</summary>
    public static async Task<bool> TryRunAdminCommandAsync(this WebApplication app, string[] args)
    {
        var flag = Array.FindIndex(args, a => string.Equals(a, RecoveryCodeFlag, StringComparison.OrdinalIgnoreCase));
        if (flag < 0)
        {
            return false;
        }

        var email = flag + 1 < args.Length ? args[flag + 1] : null;
        Environment.ExitCode = await RunRecoveryCodeAsync(app.Services, email, Console.Out, Console.Error, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Makes a recovery code for the one account with <paramref name="email"/> and prints it. Returns the process exit code: 0 when a code was
    /// printed. An unknown email (or one two accounts share) is an error, and nothing is issued.
    /// </summary>
    public static async Task<int> RunRecoveryCodeAsync(IServiceProvider services, string? email, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(email, cancellationToken);
            if (!issued.IsSuccess)
            {
                await error.WriteLineAsync(issued.Error!.Message + $" Usage: {RecoveryCodeFlag} <email of the account>");
                return 1;
            }

            await output.WriteLineAsync(
                $"Recovery code: {issued.Value.Code}{Environment.NewLine}"
                + "Give it to the account's owner. They open the web app's sign-in page, choose \"Can't sign in?\" and enter it, then sign in with "
                + $"the Google or Apple account they want to use. It works once, for that account only, and expires at {issued.Value.ExpiresAt.ToString("u", CultureInfo.InvariantCulture)}.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await error.WriteLineAsync($"Couldn't make a recovery code: {ex.Message}");
            return 1;
        }
    }
}
