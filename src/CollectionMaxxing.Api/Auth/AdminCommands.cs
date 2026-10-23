using System.Globalization;
using CollectionMaxxing.Application.Accounts;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Support commands for the person at the host, run as arguments of the API itself (docs/07, "Recovery"; docs/15):
/// <c>dotnet run --project src/CollectionMaxxing.Api -- --recovery-code someone@example.com</c>, or <c>cm-admin recovery-code
/// someone@example.com</c> in the container. Access to the host is the recovery factor; it isn't an app role. A command prints its result
/// and the program exits without serving. <c>--dev-tools grant|revoke &lt;email&gt;</c> (<c>cm-admin dev-tools ...</c>) gives or takes away the
/// platform role behind <c>/api/admin/*</c>; nothing in the app can.
/// </summary>
public static class AdminCommands
{
    public const string RecoveryCodeFlag = "--recovery-code";
    public const string DevToolsFlag = "--dev-tools";

    /// <summary>Runs the command named in <paramref name="args"/>, if any. Returns true when it did, so the caller can exit.</summary>
    public static async Task<bool> TryRunAdminCommandAsync(this WebApplication app, string[] args)
    {
        var devTools = Array.FindIndex(args, a => string.Equals(a, DevToolsFlag, StringComparison.OrdinalIgnoreCase));
        if (devTools >= 0)
        {
            Environment.ExitCode = await RunDevToolsAsync(
                app.Services,
                devTools + 1 < args.Length ? args[devTools + 1] : null,
                devTools + 2 < args.Length ? args[devTools + 2] : null,
                Console.Out,
                Console.Error,
                CancellationToken.None);
            return true;
        }

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

    /// <summary>
    /// <c>--dev-tools grant|revoke &lt;email&gt;</c>: gives or takes away the developer-tools role (the platform <c>admin</c> role) of the one
    /// account with that email. Returns the process exit code: 0 when it was done. An unknown email, one two accounts share, or anything but
    /// <c>grant</c> and <c>revoke</c> is an error, and nothing changes.
    /// </summary>
    public static async Task<int> RunDevToolsAsync(IServiceProvider services, string? action, string? email, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var usage = $"Usage: {DevToolsFlag} grant|revoke <email of the account>";
        var grant = string.Equals(action, "grant", StringComparison.OrdinalIgnoreCase);
        if (!grant && !string.Equals(action, "revoke", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync("Say whether to grant or revoke the developer tools. " + usage);
            return 1;
        }

        try
        {
            using var scope = services.CreateScope();
            var done = await scope.ServiceProvider.GetRequiredService<SetDevTools.Handler>().HandleAsync(grant, email, cancellationToken);
            if (!done.IsSuccess)
            {
                await error.WriteLineAsync(done.Error!.Message + " " + usage);
                return 1;
            }

            await output.WriteLineAsync(grant
                ? $"Developer tools granted to {done.Value}. They show the next time that person signs in."
                : $"Developer tools revoked from {done.Value}. Any browser that was signed in with it was signed out, so the role is gone now.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await error.WriteLineAsync($"Couldn't change the developer tools: {ex.Message}");
            return 1;
        }
    }
}
